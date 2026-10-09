// SlopTank modification notice: added or changed by SlopTank on 2026-10-04, 2026-10-08, 2026-10-09.
// allow-no-test-found: private integration coverage is assigned to sloptank-tests/server/tests.
#pragma warning disable CS1591
#pragma warning disable SA1116
#pragma warning disable SA1117
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Permalinks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emby.Server.Implementations.PlaybackPreparation;

public sealed class PlaybackPreparationService : IPlaybackPreparationService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ResultGates = new();
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IServerApplicationHost _appHost;
    private readonly IServiceProvider _provider;
    private readonly TimeProvider _timeProvider;

    public PlaybackPreparationService(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IServerApplicationHost appHost,
        IServiceProvider provider,
        TimeProvider timeProvider)
    {
        _dbProvider = dbProvider;
        _appHost = appHost;
        _provider = provider;
        _timeProvider = timeProvider;
    }

    public async Task<PlaybackPreparationRequest?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var activePhases = new[] { PlaybackPreparationAttemptPhase.Claimed, PlaybackPreparationAttemptPhase.Preparing };
        await db.PlaybackPreparationAttempts
            .Where(value => activePhases.Contains(value.Phase) && value.LeaseExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Phase, PlaybackPreparationAttemptPhase.Expired)
                .SetProperty(value => value.CompletedAtUtc, now), cancellationToken)
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
        if (!IsSupportedWorkerPath(path))
        {
            await RecordUnprobeableAsync(
                db,
                item,
                path,
                PermalinkObjectIdentity.Read(path) ?? "unsupported-media-path",
                "unsupported_media_path",
                now,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            RefreshCachedItem(item.Id);
            return null;
        }

        var identity = PermalinkObjectIdentity.Read(path);
        if (identity is null)
        {
            await RecordUnprobeableAsync(
                db,
                item,
                path,
                "identity-unavailable",
                "planner_unprobeable",
                now,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            RefreshCachedItem(item.Id);
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

    private static bool IsSupportedWorkerPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return File.Exists(path)
                && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task RecordUnprobeableAsync(
        JellyfinDbContext db,
        BaseItemEntity item,
        string path,
        string requestedIdentity,
        string reasonCode,
        DateTime now,
        CancellationToken cancellationToken)
    {
        item.PlaybackReadiness = PlaybackReadiness.Failed;
        item.PlaybackReadyObjectIdentity = null;
        item.PlaybackReadyAlgorithmVersion = null;
        db.PlaybackPreparationAttempts.Add(new PlaybackPreparationAttemptEntity
        {
            RequestId = Guid.NewGuid(),
            ItemId = item.Id,
            NonceHash = HashNonce(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()),
            RequestedObjectIdentity = requestedIdentity,
            RequestedPath = path,
            Phase = PlaybackPreparationAttemptPhase.Failed,
            CreatedAtUtc = now,
            LeaseExpiresAtUtc = now,
            Outcome = PlaybackPreparationContract.UnprobeableOutcome,
            ReasonCode = reasonCode,
            CompletedAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
        var currentIdentity = item?.Path is null ? null : PermalinkObjectIdentity.Read(_appHost.ExpandVirtualPath(item.Path));
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
            RefreshCachedItem(attempt.ItemId);
            throw new PlaybackPreparationException(409, "media-changed", "The media object changed after claim.");
        }

        var consumed = await db.PlaybackPreparationAttempts
            .Where(value => value.RequestId.Equals(requestId)
                && value.Phase == PlaybackPreparationAttemptPhase.Claimed
                && value.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Phase, PlaybackPreparationAttemptPhase.Preparing), cancellationToken)
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

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        RefreshCachedItem(item!.Id);
    }

    public async Task<bool> VerifyPlaybackReadinessAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item.PlaybackReadiness != PlaybackReadiness.Ready)
        {
            return false;
        }

        // Ready rows created before attestation deliberately remain visible until audit.
        if (string.IsNullOrEmpty(item.PlaybackReadyObjectIdentity))
        {
            return true;
        }

        var currentIdentity = item.Path is null
            ? null
            : PermalinkObjectIdentity.Read(_appHost.ExpandVirtualPath(item.Path));
        var valid = string.Equals(
                item.PlaybackReadyAlgorithmVersion,
                PlaybackPreparationContract.PlannerAlgorithmVersion,
                StringComparison.Ordinal)
            && currentIdentity is not null
            && string.Equals(currentIdentity, item.PlaybackReadyObjectIdentity, StringComparison.Ordinal);
        if (valid)
        {
            return true;
        }

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.BaseItems
            .Where(value => value.Id.Equals(item.Id)
                && value.PlaybackReadiness == PlaybackReadiness.Ready
                && value.PlaybackReadyObjectIdentity == item.PlaybackReadyObjectIdentity
                && value.PlaybackReadyAlgorithmVersion == item.PlaybackReadyAlgorithmVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.PlaybackReadiness, PlaybackReadiness.Pending)
                .SetProperty(value => value.PlaybackReadyObjectIdentity, (string?)null)
                .SetProperty(value => value.PlaybackReadyAlgorithmVersion, (string?)null), cancellationToken)
            .ConfigureAwait(false);
        RefreshCachedItem(item.Id);
        return false;
    }

    public async Task InvalidateReadinessIfChangedAsync(
        System.Collections.Generic.IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.BaseItems.Where(value => itemIds.Contains(value.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var invalidated = new List<Guid>();
        foreach (var item in items)
        {
            if (item.PlaybackReadiness != PlaybackReadiness.Ready
                || string.IsNullOrEmpty(item.PlaybackReadyObjectIdentity)
                || item.Path is null)
            {
                continue;
            }

            var current = PermalinkObjectIdentity.Read(_appHost.ExpandVirtualPath(item.Path));
            if (!string.Equals(item.PlaybackReadyAlgorithmVersion, PlaybackPreparationContract.PlannerAlgorithmVersion, StringComparison.Ordinal)
                || !string.Equals(current, item.PlaybackReadyObjectIdentity, StringComparison.Ordinal))
            {
                item.PlaybackReadiness = PlaybackReadiness.Pending;
                item.PlaybackReadyObjectIdentity = null;
                item.PlaybackReadyAlgorithmVersion = null;
                invalidated.Add(item.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invalidatedId in invalidated)
        {
            RefreshCachedItem(invalidatedId);
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

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = items.Select(value => value.Id).ToArray();
        var persistedItems = await db.BaseItems.Where(value => itemIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        return items
            .Where(candidate => persistedItems.TryGetValue(candidate.Id, out var persisted)
                && persisted.PlaybackReadiness == PlaybackReadiness.Ready
                && !string.IsNullOrEmpty(persisted.PlaybackReadyObjectIdentity)
                && candidate.Path is not null
                && (!string.Equals(persisted.PlaybackReadyAlgorithmVersion, PlaybackPreparationContract.PlannerAlgorithmVersion, StringComparison.Ordinal)
                    || !string.Equals(
                        PermalinkObjectIdentity.Read(_appHost.ExpandVirtualPath(candidate.Path!)),
                        persisted.PlaybackReadyObjectIdentity,
                        StringComparison.Ordinal)))
            .Select(item => item.Id)
            .ToArray();
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

    /// <summary>
    /// Re-registers a freshly materialized item after a committed readiness change. The
    /// library manager caches item instances, and the playback gate plus the visible-item
    /// paths resolve that cached instance; without this refresh a committed transition
    /// (Pending to Ready, Ready to Pending, or Failed) stays invisible to the warm server
    /// until a restart, leaving a listed item playback-refused or a demoted item admitted.
    /// The library manager is resolved lazily because it already consumes this service,
    /// so a constructor dependency would be a validated circular dependency.
    /// </summary>
    /// <param name="itemId">The item whose persisted readiness changed.</param>
    private void RefreshCachedItem(Guid itemId)
    {
        var libraryManager = _provider.GetRequiredService<ILibraryManager>();
        var fresh = libraryManager.RetrieveItem(itemId);
        if (fresh is not null)
        {
            libraryManager.RegisterItem(fresh);
        }
    }

    public async Task RenewAsync(Guid requestId, PlaybackPreparationRequest request, CancellationToken cancellationToken)
    {
        var gate = ResultGates.GetOrAdd(requestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var attempt = await LoadBoundPreparingAttemptAsync(db, requestId, request, now, cancellationToken).ConfigureAwait(false);
            attempt.LeaseExpiresAtUtc = now + LeaseDuration;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var attempt = await LoadBoundPreparingAttemptAsync(db, requestId, request, now, cancellationToken).ConfigureAwait(false);
            var item = await db.BaseItems.SingleOrDefaultAsync(value => value.Id.Equals(attempt.ItemId), cancellationToken).ConfigureAwait(false);
            var path = item?.Path is null ? null : _appHost.ExpandVirtualPath(item.Path);
            var identity = path is null ? null : PermalinkObjectIdentity.Read(path);
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
                RefreshCachedItem(attempt.ItemId);
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
