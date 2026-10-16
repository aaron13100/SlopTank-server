// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
#pragma warning disable RS0030 // Do not use banned APIs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Library
{
    /// <summary>
    /// Playback progress revisions and offline progress operation receipts (c203).
    /// See docs/sloptank-c203/progress-receipts-design.md in the SlopTank Android client.
    /// </summary>
    public partial class UserDataManager
    {
        /// <summary>
        /// The maximum number of receipts retained per user and device before compaction.
        /// </summary>
        public const int MaxReceiptsPerDevice = 1024;

        /// <summary>
        /// The maximum number of devices per user that may hold receipts.
        /// </summary>
        public const int MaxReceiptDevicesPerUser = 64;

        /// <summary>
        /// The largest play count increment a single operation may carry.
        /// </summary>
        public const int MaxPlayCountIncrement = 100;

        private const int OutcomeApplied = (int)PlaybackProgressOperationOutcome.Applied;
        private const int OutcomeConflict = (int)PlaybackProgressOperationOutcome.Conflict;

        /// <inheritdoc />
        public PlaybackProgressStateDto GetPlaybackProgressState(User user, BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(item);

            using var dbContext = _repository.CreateDbContext();
            using var transaction = dbContext.Database.BeginTransaction();
            var revision = ReadPlaybackRevision(dbContext, user.Id, item.Id);
            var stored = ReadStoredUserData(dbContext, user, item);
            transaction.Commit();
            return ToState(item.Id, revision, stored);
        }

        /// <inheritdoc />
        public PlaybackProgressOperationResultDto ApplyPlaybackProgressOperation(
            User user,
            BaseItem item,
            string deviceId,
            PlaybackProgressOperationDto operation)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ArgumentNullException.ThrowIfNull(operation);
            ValidateOperation(operation);

            var payloadHash = ComputePayloadHash(item.Id, deviceId, operation);
            UserItemData committed;
            PlaybackProgressOperationResultDto result;

            using (var dbContext = _repository.CreateDbContext())
            using (var transaction = dbContext.Database.BeginTransaction())
            {
                // 1. Receipt replay: an identical retry returns the recorded outcome without writing.
                var receipt = dbContext.PlaybackProgressReceipts
                    .AsNoTracking()
                    .FirstOrDefault(r => r.UserId == user.Id && r.DeviceId == deviceId && r.OperationId == operation.OperationId);
                if (receipt is not null)
                {
                    return string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal)
                        ? FromReceipt(receipt)
                        : Refusal(PlaybackProgressOperationOutcome.PayloadMismatch, operation);
                }

                // 2. Retention watermark and sequence uniqueness.
                var watermark = dbContext.PlaybackProgressDeviceWatermarks
                    .FirstOrDefault(w => w.UserId == user.Id && w.DeviceId == deviceId);
                if (watermark is not null && operation.Sequence <= watermark.CompactedThroughSequence)
                {
                    return Refusal(PlaybackProgressOperationOutcome.Expired, operation);
                }

                if (dbContext.PlaybackProgressReceipts.Any(r => r.UserId == user.Id && r.DeviceId == deviceId && r.Sequence == operation.Sequence))
                {
                    return Refusal(PlaybackProgressOperationOutcome.PayloadMismatch, operation);
                }

                // 3. Bounded growth: a new device needs a free slot.
                if (watermark is null)
                {
                    var devices = dbContext.PlaybackProgressDeviceWatermarks.Count(w => w.UserId == user.Id);
                    if (devices >= MaxReceiptDevicesPerUser)
                    {
                        return Refusal(PlaybackProgressOperationOutcome.CapacityExceeded, operation);
                    }

                    watermark = new PlaybackProgressDeviceWatermark { UserId = user.Id, DeviceId = deviceId };
                    dbContext.PlaybackProgressDeviceWatermarks.Add(watermark);
                }

                // 4. The compare: the revision must still equal the client's acknowledged baseline.
                var currentRevision = ReadPlaybackRevision(dbContext, user.Id, item.Id);
                var stored = ReadStoredUserData(dbContext, user, item);
                if (currentRevision != operation.BaseRevision)
                {
                    var conflict = NewReceipt(user.Id, deviceId, item.Id, operation, payloadHash, OutcomeConflict, currentRevision, stored);
                    dbContext.PlaybackProgressReceipts.Add(conflict);
                    dbContext.SaveChanges();
                    CompactReceipts(dbContext, watermark, operation.OperationId);
                    transaction.Commit();
                    return FromReceipt(conflict, replayed: false);
                }

                // 5. The set: playback rows, revision and receipt in this one transaction.
                var updated = Copy(stored);
                updated.PlaybackPositionTicks = operation.PositionTicks;
                updated.Played = operation.Played;
                updated.PlayCount = stored.PlayCount + operation.PlayCountIncrement;
                if (operation.LastPlayedDate.HasValue)
                {
                    updated.LastPlayedDate = operation.LastPlayedDate.Value.ToUniversalTime();
                }
                else if (operation.PlayCountIncrement > 0)
                {
                    updated.LastPlayedDate = DateTime.UtcNow;
                }

                UpsertUserDataRows(dbContext, user, item, updated);
                dbContext.SaveChanges();

                var newRevision = operation.BaseRevision + 1;
                if (!TryAdvancePlaybackRevision(dbContext, user.Id, item.Id, operation.BaseRevision))
                {
                    // Unreachable while the write lock is held; never commit a write that lost its predicate.
                    throw new InvalidOperationException("The playback revision changed inside the operation transaction.");
                }

                var applied = NewReceipt(user.Id, deviceId, item.Id, operation, payloadHash, OutcomeApplied, newRevision, updated);
                dbContext.PlaybackProgressReceipts.Add(applied);
                dbContext.SaveChanges();
                CompactReceipts(dbContext, watermark, operation.OperationId);
                transaction.Commit();

                committed = updated;
                result = FromReceipt(applied, replayed: false);
                item.UserData = dbContext.UserData.Where(e => e.ItemId == item.Id).AsNoTracking().ToArray();
            }

            // Only after the commit: refresh the cache with a private copy and notify listeners.
            _cache.AddOrUpdate(GetCacheKey(user.InternalId, item.Id), Copy(committed));
            UserDataSaved?.Invoke(this, new UserDataSaveEventArgs
            {
                Keys = item.GetUserDataKeys(),
                UserData = Copy(committed),
                SaveReason = UserDataSaveReason.UpdateUserData,
                UserId = user.Id,
                Item = item
            });

            return result;
        }

        /// <summary>
        /// Computes the canonical, versioned payload hash that binds a receipt to its request.
        /// </summary>
        /// <param name="itemId">The target item.</param>
        /// <param name="deviceId">The authenticated device id.</param>
        /// <param name="operation">The operation.</param>
        /// <returns>The lowercase hex SHA-256 hash.</returns>
        internal static string ComputePayloadHash(Guid itemId, string deviceId, PlaybackProgressOperationDto operation)
        {
            var lastPlayed = operation.LastPlayedDate.HasValue
                ? operation.LastPlayedDate.Value.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)
                : "-";
            var canonical = string.Join(
                '\n',
                "c203-progress-op/v1",
                itemId.ToString("N", CultureInfo.InvariantCulture),
                operation.ServerId,
                deviceId,
                operation.OperationId.ToString("N", CultureInfo.InvariantCulture),
                operation.Sequence.ToString(CultureInfo.InvariantCulture),
                operation.BaseRevision.ToString(CultureInfo.InvariantCulture),
                operation.PositionTicks.ToString(CultureInfo.InvariantCulture),
                operation.Played ? "1" : "0",
                operation.PlayCountIncrement.ToString(CultureInfo.InvariantCulture),
                lastPlayed);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static void ValidateOperation(PlaybackProgressOperationDto operation)
        {
            if (operation.OperationId == Guid.Empty)
            {
                throw new ArgumentException("OperationId is required.", nameof(operation));
            }

            if (operation.Sequence < 1)
            {
                throw new ArgumentException("Sequence must be at least 1.", nameof(operation));
            }

            if (operation.BaseRevision < 0 || operation.PositionTicks < 0)
            {
                throw new ArgumentException("BaseRevision and PositionTicks must not be negative.", nameof(operation));
            }

            if (operation.PlayCountIncrement is < 0 or > MaxPlayCountIncrement)
            {
                throw new ArgumentException("PlayCountIncrement is out of range.", nameof(operation));
            }
        }

        /// <summary>
        /// Gets a value indicating whether a save reason always advances the playback revision,
        /// even when the values are unchanged (this is what defeats ABA for live playback).
        /// </summary>
        private static bool AlwaysAdvancesPlaybackRevision(UserDataSaveReason reason)
            => reason is UserDataSaveReason.PlaybackStart
                or UserDataSaveReason.PlaybackProgress
                or UserDataSaveReason.PlaybackFinished
                or UserDataSaveReason.TogglePlayed
                or UserDataSaveReason.ResumeCleared;

        private static bool PlaybackFieldsDiffer(IReadOnlyList<UserData> storedRows, IReadOnlyList<string> keys, UserItemData userData)
        {
            var relevant = storedRows.Where(r => keys.Contains(r.CustomDataKey)).ToList();
            if (relevant.Count == 0)
            {
                return userData.PlaybackPositionTicks != 0
                    || userData.Played
                    || userData.PlayCount != 0
                    || userData.LastPlayedDate.HasValue;
            }

            return relevant.Exists(r => r.PlaybackPositionTicks != userData.PlaybackPositionTicks
                || r.Played != userData.Played
                || r.PlayCount != userData.PlayCount
                || r.LastPlayedDate != userData.LastPlayedDate);
        }

        private static long ReadPlaybackRevision(JellyfinDbContext dbContext, Guid userId, Guid itemId)
            => dbContext.PlaybackProgressRevisions
                .AsNoTracking()
                .Where(r => r.UserId == userId && r.ItemId == itemId)
                .Select(r => (long?)r.Revision)
                .FirstOrDefault() ?? 0;

        /// <summary>
        /// Unconditionally advances the playback revision inside the caller's transaction.
        /// </summary>
        private static void AdvancePlaybackRevision(JellyfinDbContext dbContext, Guid userId, Guid itemId)
        {
            var rows = dbContext.PlaybackProgressRevisions
                .Where(r => r.UserId == userId && r.ItemId == itemId)
                .ExecuteUpdate(setters => setters.SetProperty(r => r.Revision, r => r.Revision + 1));
            if (rows == 0)
            {
                dbContext.PlaybackProgressRevisions.Add(new PlaybackProgressRevision { UserId = userId, ItemId = itemId, Revision = 1 });
                dbContext.SaveChanges();
            }
        }

        /// <summary>
        /// Advances the playback revision only if it still equals <paramref name="expected"/>.
        /// </summary>
        private static bool TryAdvancePlaybackRevision(JellyfinDbContext dbContext, Guid userId, Guid itemId, long expected)
        {
            var rows = dbContext.PlaybackProgressRevisions
                .Where(r => r.UserId == userId && r.ItemId == itemId && r.Revision == expected)
                .ExecuteUpdate(setters => setters.SetProperty(r => r.Revision, r => r.Revision + 1));
            if (rows == 1)
            {
                return true;
            }

            if (expected != 0 || dbContext.PlaybackProgressRevisions.Any(r => r.UserId == userId && r.ItemId == itemId))
            {
                return false;
            }

            dbContext.PlaybackProgressRevisions.Add(new PlaybackProgressRevision { UserId = userId, ItemId = itemId, Revision = 1 });
            dbContext.SaveChanges();
            return true;
        }

        private static UserItemData Copy(UserItemData source)
            => new()
            {
                Key = source.Key,
                Rating = source.Rating,
                PlaybackPositionTicks = source.PlaybackPositionTicks,
                PlayCount = source.PlayCount,
                IsFavorite = source.IsFavorite,
                LastPlayedDate = source.LastPlayedDate,
                Played = source.Played,
                AudioStreamIndex = source.AudioStreamIndex,
                SubtitleStreamIndex = source.SubtitleStreamIndex,
            };

        private static PlaybackProgressStateDto ToState(Guid itemId, long revision, UserItemData data)
            => new()
            {
                ItemId = itemId,
                Revision = revision,
                PositionTicks = data.PlaybackPositionTicks,
                Played = data.Played,
                PlayCount = data.PlayCount,
                LastPlayedDate = data.LastPlayedDate,
            };

        private static PlaybackProgressReceipt NewReceipt(
            Guid userId,
            string deviceId,
            Guid itemId,
            PlaybackProgressOperationDto operation,
            string payloadHash,
            int outcome,
            long revision,
            UserItemData state)
            => new()
            {
                UserId = userId,
                DeviceId = deviceId,
                OperationId = operation.OperationId,
                Sequence = operation.Sequence,
                ItemId = itemId,
                PayloadHash = payloadHash,
                Outcome = outcome,
                ResultRevision = revision,
                ResultPositionTicks = state.PlaybackPositionTicks,
                ResultPlayed = state.Played,
                ResultPlayCount = state.PlayCount,
                ResultLastPlayedDate = state.LastPlayedDate,
                CreatedAtUtc = DateTime.UtcNow,
            };

        private static PlaybackProgressOperationResultDto FromReceipt(PlaybackProgressReceipt receipt, bool replayed = true)
            => new()
            {
                Outcome = (PlaybackProgressOperationOutcome)receipt.Outcome,
                Replayed = replayed,
                OperationId = receipt.OperationId,
                State = new PlaybackProgressStateDto
                {
                    ItemId = receipt.ItemId,
                    Revision = receipt.ResultRevision,
                    PositionTicks = receipt.ResultPositionTicks,
                    Played = receipt.ResultPlayed,
                    PlayCount = receipt.ResultPlayCount,
                    LastPlayedDate = receipt.ResultLastPlayedDate,
                },
            };

        private static PlaybackProgressOperationResultDto Refusal(PlaybackProgressOperationOutcome outcome, PlaybackProgressOperationDto operation)
            => new() { Outcome = outcome, OperationId = operation.OperationId };

        /// <summary>
        /// Deletes the oldest receipts beyond <see cref="MaxReceiptsPerDevice"/> and raises the
        /// device watermark over them, in the caller's transaction. The receipt just written is
        /// never compacted, so its own retry still replays.
        /// </summary>
        private static void CompactReceipts(JellyfinDbContext dbContext, PlaybackProgressDeviceWatermark watermark, Guid currentOperationId)
        {
            var count = dbContext.PlaybackProgressReceipts.Count(r => r.UserId == watermark.UserId && r.DeviceId == watermark.DeviceId);
            var excess = count - MaxReceiptsPerDevice;
            if (excess <= 0)
            {
                return;
            }

            var oldest = dbContext.PlaybackProgressReceipts
                .Where(r => r.UserId == watermark.UserId && r.DeviceId == watermark.DeviceId && r.OperationId != currentOperationId)
                .OrderBy(r => r.Sequence)
                .Take(excess)
                .ToList();
            if (oldest.Count == 0)
            {
                return;
            }

            watermark.CompactedThroughSequence = Math.Max(watermark.CompactedThroughSequence, oldest.Max(r => r.Sequence));
            dbContext.PlaybackProgressReceipts.RemoveRange(oldest);
            dbContext.SaveChanges();
        }

        private UserItemData ReadStoredUserData(JellyfinDbContext dbContext, User user, BaseItem item)
        {
            var rows = dbContext.UserData
                .AsNoTracking()
                .Where(e => e.ItemId == item.Id && e.UserId == user.Id)
                .ToList();
            var direct = rows.FirstOrDefault(e => e.CustomDataKey == item.Id.ToString("N", CultureInfo.InvariantCulture));
            var chosen = direct ?? rows.FirstOrDefault();
            if (chosen is not null)
            {
                return Map(chosen);
            }

            var keys = item.GetUserDataKeys();
            return new UserItemData { Key = keys.Count > 0 ? keys[0] : item.Id.ToString("N", CultureInfo.InvariantCulture) };
        }

        private void UpsertUserDataRows(JellyfinDbContext dbContext, User user, BaseItem item, UserItemData data)
        {
            foreach (var key in item.GetUserDataKeys())
            {
                var row = Copy(data);
                row.Key = key;
                var entry = Map(row, user.Id, item.Id);
                if (dbContext.UserData.Any(f => f.ItemId == entry.ItemId && f.UserId == entry.UserId && f.CustomDataKey == entry.CustomDataKey))
                {
                    dbContext.UserData.Attach(entry).State = EntityState.Modified;
                }
                else
                {
                    dbContext.UserData.Add(entry);
                }
            }
        }
    }
}
