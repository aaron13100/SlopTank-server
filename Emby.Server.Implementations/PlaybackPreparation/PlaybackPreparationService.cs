// SlopTank modification notice: added or changed by SlopTank on 2026-10-04, 2026-10-14, 2026-10-31, 2026-11-03, 2026-11-04, 2026-11-11.
// allow-no-test-found: private integration coverage is assigned to sloptank-tests/server/tests.
#pragma warning disable CS1591
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.PlaybackPreparation;

public sealed class PlaybackPreparationService : IPlaybackPreparationService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ResultGates = new();
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IServerApplicationHost _appHost;
    private readonly TimeProvider _timeProvider;

    // Resolved lazily: the library manager depends on this service, so it cannot be injected.
    private readonly Lazy<ILibraryManager?> _library;

    public PlaybackPreparationService(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IServerApplicationHost appHost,
        TimeProvider timeProvider)
    {
        _dbProvider = dbProvider;
        _appHost = appHost;
        _timeProvider = timeProvider;
        _library = new Lazy<ILibraryManager?>(() => appHost.Resolve<ILibraryManager>());
    }

    public async Task<PlaybackPreparationRequest?> ClaimAsync(CancellationToken cancellationToken)
    {
        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var activePhases = new[] { PlaybackPreparationAttemptPhase.Claimed, PlaybackPreparationAttemptPhase.Preparing };
                await db.PlaybackPreparationAttempts
                    .Where(value => activePhases.Contains(value.Phase) && value.LeaseExpiresAtUtc <= now)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(value => value.Phase, PlaybackPreparationAttemptPhase.Expired)
                            .SetProperty(value => value.CompletedAtUtc, now),
                        cancellationToken)
                    .ConfigureAwait(false);

                var item = await db.BaseItems
                    .Where(value => value.IngestState == IngestState.Complete
                        && (value.Type == "MediaBrowser.Controller.Entities.Movies.Movie"
                            || value.Type == "MediaBrowser.Controller.Entities.TV.Episode")
                        && (value.PlaybackReadiness == PlaybackReadiness.Pending
                            || value.PlaybackReadiness == PlaybackReadiness.Failed)
                        && value.Path != null
                        && !db.PlaybackPreparationAttempts.Any(attempt =>
                            attempt.ItemId.Equals(value.Id)
                            && attempt.Phase == PlaybackPreparationAttemptPhase.Failed
                            && attempt.CompletedAtUtc != null
                            && attempt.CompletedAtUtc > now.AddMinutes(-5))
                        && !db.PlaybackPreparationAttempts.Any(attempt =>
                            attempt.ItemId.Equals(value.Id)
                            && activePhases.Contains(attempt.Phase)
                            && attempt.LeaseExpiresAtUtc > now))
                    .Select(value => new
                    {
                        Item = value,
                        LastFailure = db.PlaybackPreparationAttempts
                            .Where(attempt => attempt.ItemId.Equals(value.Id)
                                && attempt.Phase == PlaybackPreparationAttemptPhase.Failed)
                            .OrderByDescending(attempt => attempt.CompletedAtUtc)
                            .Select(attempt => attempt.CompletedAtUtc)
                            .FirstOrDefault()
                    })
                    .OrderBy(value => value.Item.PlaybackReadiness == PlaybackReadiness.Pending ? 0 : 1)
                    .ThenBy(value => value.Item.PlaybackReadiness == PlaybackReadiness.Pending ? value.Item.DateCreated : value.LastFailure)
                    .ThenBy(value => value.Item.Id)
                    .Select(value => value.Item)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (item is null || item.Path is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                var path = _appHost.ExpandVirtualPath(item.Path);
                var identity = MediaFileFingerprint.Read(path);
                if (identity is null)
                {
                    item.PlaybackReadiness = PlaybackReadiness.Failed;
                    item.PlaybackReadyObjectIdentity = null;
                    item.PlaybackReadyAlgorithmVersion = null;
                    db.PlaybackPreparationAttempts.Add(new PlaybackPreparationAttemptEntity
                    {
                        RequestId = Guid.NewGuid(),
                        ItemId = item.Id,
                        NonceHash = HashNonce(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()),
                        RequestedObjectIdentity = "identity-unavailable",
                        RequestedPath = path,
                        Phase = PlaybackPreparationAttemptPhase.Failed,
                        CreatedAtUtc = now,
                        LeaseExpiresAtUtc = now,
                        Outcome = PlaybackPreparationContract.UnprobeableOutcome,
                        ReasonCode = "planner_unprobeable",
                        CompletedAtUtc = now
                    });
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    SyncRegisteredItem(item);
                    return null;
                }

                var requestId = Guid.NewGuid();
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var leaseExpires = now + LeaseDuration;
                db.PlaybackPreparationAttempts.Add(new PlaybackPreparationAttemptEntity
                {
                    RequestId = requestId,
                    ItemId = item.Id,
                    NonceHash = HashNonce(nonce),
                    RequestedObjectIdentity = identity,
                    RequestedPath = path,
                    Phase = PlaybackPreparationAttemptPhase.Claimed,
                    CreatedAtUtc = now,
                    LeaseExpiresAtUtc = leaseExpires
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return NewRequest(requestId, nonce, item.Id, path, identity);
            }
        }
    }

    public async Task SubmitResultAsync(Guid requestId, PlaybackPreparationResult result, CancellationToken cancellationToken)
    {
        var gate = ResultGates.GetOrAdd(requestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SubmitResultCoreAsync(requestId, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SubmitResultCoreAsync(Guid requestId, PlaybackPreparationResult result, CancellationToken cancellationToken)
    {
        var shapeRequest = new PlaybackPreparationRequest
        {
            ContractType = result?.ContractType,
            ContractVersion = result?.ContractVersion ?? 0,
            PlannerAlgorithmVersion = result?.PlannerAlgorithmVersion,
            RequestId = result?.RequestId,
            Nonce = result?.Nonce,
            ItemId = result?.ItemId,
            Path = result?.Path,
            MediaSignature = result?.MediaSignature
        };
        if (!PlaybackPreparationContractValidator.TryValidateResult(shapeRequest, result, out var validationError))
        {
            throw new PlaybackPreparationException(400, "invalid-result", validationError ?? "Result contract is invalid.");
        }

        if (!Guid.TryParse(result!.RequestId, out var echoedRequestId) || !echoedRequestId.Equals(requestId))
        {
            throw new PlaybackPreparationException(409, "request-binding-mismatch", "The result requestId is not bound to this route.");
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var attempt = await db.PlaybackPreparationAttempts.SingleOrDefaultAsync(value => value.RequestId.Equals(requestId), cancellationToken).ConfigureAwait(false);
                if (attempt is null || attempt.Phase != PlaybackPreparationAttemptPhase.Claimed)
                {
                    throw new PlaybackPreparationException(409, "lease-not-outstanding", "The preparation lease is not outstanding.");
                }

                if (attempt.LeaseExpiresAtUtc <= now)
                {
                    attempt.Phase = PlaybackPreparationAttemptPhase.Expired;
                    attempt.CompletedAtUtc = now;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    throw new PlaybackPreparationException(409, "lease-expired", "The preparation lease has expired.");
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(attempt.NonceHash),
                        Convert.FromHexString(HashNonce(result.Nonce!)))
                    || !string.Equals(result.ItemId, attempt.ItemId.ToString(), StringComparison.Ordinal)
                    || !string.Equals(result.Path, attempt.RequestedPath, StringComparison.Ordinal)
                    || !string.Equals(result.MediaSignature, attempt.RequestedObjectIdentity, StringComparison.Ordinal))
                {
                    throw new PlaybackPreparationException(409, "request-binding-mismatch", "The result does not match the leased item.");
                }

                var item = await db.BaseItems.SingleOrDefaultAsync(value => value.Id.Equals(attempt.ItemId), cancellationToken).ConfigureAwait(false);
                var currentIdentity = item?.Path is null ? null : MediaFileFingerprint.Read(_appHost.ExpandVirtualPath(item.Path));
                if (item is null || !string.Equals(currentIdentity, attempt.RequestedObjectIdentity, StringComparison.Ordinal))
                {
                    if (item is not null)
                    {
                        item.PlaybackReadiness = PlaybackReadiness.Pending;
                        item.PlaybackReadyObjectIdentity = null;
                        item.PlaybackReadyAlgorithmVersion = null;
                    }

                    attempt.Phase = PlaybackPreparationAttemptPhase.Expired;
                    attempt.CompletedAtUtc = now;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (item is not null)
                    {
                        SyncRegisteredItem(item);
                    }

                    throw new PlaybackPreparationException(409, "media-changed", "The media object changed after claim.");
                }

                var consumed = await db.PlaybackPreparationAttempts
                    .Where(value => value.RequestId.Equals(requestId)
                        && value.Phase == PlaybackPreparationAttemptPhase.Claimed
                        && value.LeaseExpiresAtUtc > now)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(value => value.Phase, PlaybackPreparationAttemptPhase.Preparing),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (consumed != 1)
                {
                    throw new PlaybackPreparationException(409, "lease-not-outstanding", "The preparation lease was already consumed.");
                }

                attempt.Outcome = result.Outcome;
                attempt.ReasonCode = result.ReasonCode;
                attempt.PlanJson = result.Plan is null ? null : JsonSerializer.Serialize(result.Plan);
                attempt.CompletedAtUtc = now;
                if (result.Outcome == PlaybackPreparationContract.CompatibleOutcome)
                {
                    item!.PlaybackReadiness = PlaybackReadiness.Ready;
                    item.PlaybackReadyObjectIdentity = currentIdentity;
                    item.PlaybackReadyAlgorithmVersion = PlaybackPreparationContract.PlannerAlgorithmVersion;
                    attempt.Phase = PlaybackPreparationAttemptPhase.Completed;
                }
                else if (result.Outcome == PlaybackPreparationContract.NeedsPreparationOutcome)
                {
                    item!.PlaybackReadiness = PlaybackReadiness.Pending;
                    attempt.Phase = PlaybackPreparationAttemptPhase.Preparing;
                    attempt.LeaseExpiresAtUtc = now + LeaseDuration;
                    attempt.CompletedAtUtc = null;
                }
                else
                {
                    item!.PlaybackReadiness = PlaybackReadiness.Failed;
                    item.PlaybackReadyObjectIdentity = null;
                    item.PlaybackReadyAlgorithmVersion = null;
                    attempt.Phase = PlaybackPreparationAttemptPhase.Failed;
                }

                // Several retained record IDs can explicitly represent this same file.
                // Compatibility belongs to those bytes; it must not leave an old ID
                // permanently pending after the canonical file completed preparation.
                var preparedItem = item!;
                var sameFileRecords = await db.BaseItems.Where(value => !value.Id.Equals(preparedItem.Id) && value.Path == preparedItem.Path
                        && (value.PrimaryVersionId.Equals(preparedItem.Id) || value.OwnerId.Equals(preparedItem.Id)))
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var alias in sameFileRecords)
                {
                    alias.PlaybackReadiness = preparedItem.PlaybackReadiness;
                    alias.PlaybackReadyObjectIdentity = preparedItem.PlaybackReadyObjectIdentity;
                    alias.PlaybackReadyAlgorithmVersion = preparedItem.PlaybackReadyAlgorithmVersion;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                SyncRegisteredItem(preparedItem);
                foreach (var alias in sameFileRecords)
                {
                    SyncRegisteredItem(alias);
                }
            }
        }
    }

    public async Task<bool> VerifyPlaybackReadinessAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item.PlaybackReadiness != PlaybackReadiness.Ready)
        {
            return false;
        }

        if (!ReadyObjectChanged(item.Path, ReadyAttestation.From(item)))
        {
            return true;
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.BaseItems
                .Where(value => value.Id.Equals(item.Id)
                    && value.PlaybackReadiness == PlaybackReadiness.Ready
                    && value.PlaybackReadyObjectIdentity == item.PlaybackReadyObjectIdentity
                    && value.PlaybackReadyAlgorithmVersion == item.PlaybackReadyAlgorithmVersion)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(value => value.PlaybackReadiness, PlaybackReadiness.Pending)
                        .SetProperty(value => value.PlaybackReadyObjectIdentity, (string?)null)
                        .SetProperty(value => value.PlaybackReadyAlgorithmVersion, (string?)null),
                    cancellationToken)
                .ConfigureAwait(false);

            // The row may already carry a newer attestation than the item we were handed, so
            // the live item mirrors whatever the row now says: a stale Ready would keep
            // refusing playback here, and a stale Pending would never be claimed again.
            var persisted = await db.BaseItems
                .Where(value => value.Id.Equals(item.Id))
                .Select(value => new { value.PlaybackReadiness, value.PlaybackReadyObjectIdentity, value.PlaybackReadyAlgorithmVersion })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (persisted is not null)
            {
                item.PlaybackReadiness = persisted.PlaybackReadiness;
                item.PlaybackReadyObjectIdentity = persisted.PlaybackReadyObjectIdentity;
                item.PlaybackReadyAlgorithmVersion = persisted.PlaybackReadyAlgorithmVersion;
            }

            return false;
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyCollection<Guid>> FindChangedReadinessAsync(
        System.Collections.Generic.IReadOnlyCollection<BaseItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var itemIds = items.Select(value => value.Id).ToArray();
            var persistedItems = await db.BaseItems.Where(value => itemIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
            return items
                .Where(candidate => persistedItems.TryGetValue(candidate.Id, out var persisted)
                    && persisted.PlaybackReadiness == PlaybackReadiness.Ready
                    && ReadyObjectChanged(candidate.Path, ReadyAttestation.From(persisted)))
                .Select(item => item.Id)
                .ToArray();
        }
    }

    /// <summary>
    /// Decides whether the file under <paramref name="currentPath"/> is still the object a
    /// Ready attestation describes. One rule serves the row about to be saved and the live
    /// item about to be played.
    /// </summary>
    private bool ReadyObjectChanged(string? currentPath, ReadyAttestation attested)
    {
        var path = currentPath is null ? null : _appHost.ExpandVirtualPath(currentPath);
        if (!string.IsNullOrEmpty(attested.ObjectIdentity))
        {
            var current = path is null ? null : MediaFileFingerprint.Read(path);
            return !string.Equals(attested.AlgorithmVersion, PlaybackPreparationContract.PlannerAlgorithmVersion, StringComparison.Ordinal)
                || !string.Equals(current, attested.ObjectIdentity, StringComparison.Ordinal);
        }

        // Rows attested before object identities existed carry only the size and modification
        // time the library recorded when it saved them. Different bytes, under the same path or
        // a new one (an mkv replaced by its mp4), are not the bytes that were attested.
        return path is not null
            && MediaFileFingerprint.DiffersFromRecorded(path, attested.RecordedSize, attested.RecordedLastWriteUtc);
    }

    /// <summary>The facts a Ready attestation rests on, read from a row or a live item.</summary>
    private readonly record struct ReadyAttestation(
        string? ObjectIdentity,
        string? AlgorithmVersion,
        long? RecordedSize,
        DateTime? RecordedLastWriteUtc)
    {
        public static ReadyAttestation From(BaseItemEntity persisted)
            => new(persisted.PlaybackReadyObjectIdentity, persisted.PlaybackReadyAlgorithmVersion, persisted.Size, persisted.DateModified);

        public static ReadyAttestation From(BaseItem item)
            => new(item.PlaybackReadyObjectIdentity, item.PlaybackReadyAlgorithmVersion, item.Size, item.DateModified);
    }

    /// <summary>
    /// Copies persisted readiness onto the registered item, which answers playback requests
    /// until a restart would otherwise reload it from the database.
    /// </summary>
    private void SyncRegisteredItem(BaseItemEntity persisted)
    {
        var registered = _library.Value?.GetRegisteredItem(persisted.Id);
        if (registered is null)
        {
            return;
        }

        registered.PlaybackReadiness = persisted.PlaybackReadiness;
        registered.PlaybackReadyObjectIdentity = persisted.PlaybackReadyObjectIdentity;
        registered.PlaybackReadyAlgorithmVersion = persisted.PlaybackReadyAlgorithmVersion;
    }

    private static PlaybackPreparationRequest NewRequest(Guid requestId, string nonce, Guid itemId, string path, string identity)
        => new()
        {
            ContractType = PlaybackPreparationContract.ContractType,
            ContractVersion = PlaybackPreparationContract.ContractVersion,
            PlannerAlgorithmVersion = PlaybackPreparationContract.PlannerAlgorithmVersion,
            RequestId = requestId.ToString(),
            Nonce = nonce,
            ItemId = itemId.ToString(),
            Path = path,
            MediaSignature = identity
        };

    private static string HashNonce(string nonce)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    public async Task RenewAsync(Guid requestId, PlaybackPreparationRequest request, CancellationToken cancellationToken)
    {
        var gate = ResultGates.GetOrAdd(requestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    var attempt = await LoadBoundPreparingAttemptAsync(db, requestId, request, now, cancellationToken).ConfigureAwait(false);
                    attempt.LeaseExpiresAtUtc = now + LeaseDuration;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackPreparationRequest> RecheckAsync(Guid requestId, PlaybackPreparationRequest request, CancellationToken cancellationToken)
    {
        var gate = ResultGates.GetOrAdd(requestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    var attempt = await LoadBoundPreparingAttemptAsync(db, requestId, request, now, cancellationToken).ConfigureAwait(false);
                    var item = await db.BaseItems.SingleOrDefaultAsync(value => value.Id.Equals(attempt.ItemId), cancellationToken).ConfigureAwait(false);
                    var path = item?.Path is null ? null : _appHost.ExpandVirtualPath(item.Path);
                    var identity = path is null ? null : MediaFileFingerprint.Read(path);
                    if (item is null || path is null || identity is null)
                    {
                        if (item is not null)
                        {
                            item.PlaybackReadiness = PlaybackReadiness.Failed;
                            item.PlaybackReadyObjectIdentity = null;
                            item.PlaybackReadyAlgorithmVersion = null;
                        }

                        attempt.Phase = PlaybackPreparationAttemptPhase.Failed;
                        attempt.Outcome = PlaybackPreparationContract.UnprobeableOutcome;
                        attempt.ReasonCode = "planner_unprobeable";
                        attempt.CompletedAtUtc = now;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        if (item is not null)
                        {
                            SyncRegisteredItem(item);
                        }

                        throw new PlaybackPreparationException(409, "media-unavailable", "The current media object cannot be identified.");
                    }

                    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                    attempt.NonceHash = HashNonce(nonce);
                    attempt.RequestedPath = path;
                    attempt.RequestedObjectIdentity = identity;
                    attempt.Phase = PlaybackPreparationAttemptPhase.Claimed;
                    attempt.Outcome = null;
                    attempt.ReasonCode = null;
                    attempt.PlanJson = null;
                    attempt.CompletedAtUtc = null;
                    attempt.LeaseExpiresAtUtc = now + LeaseDuration;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return NewRequest(requestId, nonce, attempt.ItemId, path, identity);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PlaybackPreparationAttemptEntity> LoadBoundPreparingAttemptAsync(
        JellyfinDbContext db,
        Guid requestId,
        PlaybackPreparationRequest request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!PlaybackPreparationContractValidator.TryValidateRequest(request, out var validationError))
        {
            throw new PlaybackPreparationException(400, "invalid-request", validationError ?? "Request contract is invalid.");
        }

        if (!Guid.TryParse(request.RequestId, out var echoedRequestId) || !echoedRequestId.Equals(requestId))
        {
            throw new PlaybackPreparationException(409, "request-binding-mismatch", "The requestId is not bound to this route.");
        }

        var attempt = await db.PlaybackPreparationAttempts
            .SingleOrDefaultAsync(value => value.RequestId.Equals(requestId), cancellationToken)
            .ConfigureAwait(false);
        if (attempt is null || attempt.Phase != PlaybackPreparationAttemptPhase.Preparing)
        {
            throw new PlaybackPreparationException(409, "lease-not-preparing", "The preparation lease is not in the preparing phase.");
        }

        if (attempt.LeaseExpiresAtUtc <= now)
        {
            throw new PlaybackPreparationException(409, "lease-expired", "The preparation lease has expired.");
        }

        var nonceMatches = CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(attempt.NonceHash),
            Convert.FromHexString(HashNonce(request.Nonce!)));
        if (!nonceMatches
            || !string.Equals(request.ItemId, attempt.ItemId.ToString(), StringComparison.Ordinal)
            || !string.Equals(request.Path, attempt.RequestedPath, StringComparison.Ordinal)
            || !string.Equals(request.MediaSignature, attempt.RequestedObjectIdentity, StringComparison.Ordinal))
        {
            throw new PlaybackPreparationException(409, "request-binding-mismatch", "The request does not match the preparing lease.");
        }

        return attempt;
    }
}
