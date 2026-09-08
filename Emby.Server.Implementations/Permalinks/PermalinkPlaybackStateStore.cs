using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Publishes durable playback plans and immutable per-entry snapshots.
/// </summary>
internal sealed class PermalinkPlaybackStateStore
{
    private readonly ConcurrentDictionary<string, ActivePlaybackSession> _activeSessions = new();
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkPlaybackDocumentStore _documents;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkPlaybackPlanStore _plans;
    private readonly PermalinkPlaybackRecovery _recovery;
    private readonly TimeProvider _timeProvider;

    public PermalinkPlaybackStateStore(
        PermalinkAuthorityStore authority,
        PermalinkPlaybackDocumentStore documents,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkPlaybackPlanStore plans,
        PermalinkPlaybackRecovery recovery,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _documents = documents;
        _fileSystem = fileSystem;
        _plans = plans;
        _recovery = recovery;
        _timeProvider = timeProvider;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        _ = await _recovery.ReclaimAsync(
            IsActiveHandle,
            cancellationToken).ConfigureAwait(false);
    }

    public bool IsActiveSession(string handle, string playbackSessionId, Guid userId)
    {
        return _activeSessions.TryGetValue(handle, out var session)
            && string.Equals(
                session.PlaybackSessionId,
                playbackSessionId,
                StringComparison.Ordinal)
            && session.UserId.Equals(userId);
    }

    public async Task<PermalinkPlaybackSnapshot> AdmitAsync(
        string handle,
        string playbackSessionId,
        Guid userId,
        Guid serverId,
        IReadOnlyList<PermalinkPlaybackPlanStore.PlaybackPlanEntry> entries,
        int queueOrdinal,
        bool complete,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playbackSessionId)
            || entries.Count == 0
            || queueOrdinal < 0
            || queueOrdinal >= entries.Count)
        {
            throw Conflict(
                "playback-ordinal-invalid",
                "Playback session, plan entries, and an in-range queue ordinal are required.");
        }

        var admissionLock = GetAdmissionLock(handle);
        await admissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Root(handle);
            _fileSystem.CreateDirectoryDurable(root);
            var consumedPath = Path.Combine(root, "consumed.json");
            var plan = await _plans.ReadOrPublishAsync(
                root,
                handle,
                userId,
                serverId,
                entries,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(consumedPath))
            {
                return await ContinueSessionAsync(
                    root,
                    handle,
                    playbackSessionId,
                    userId,
                    serverId,
                    plan,
                    queueOrdinal,
                    entries.Count,
                    complete,
                    cancellationToken).ConfigureAwait(false);
            }

            return await StartSessionAsync(
                root,
                handle,
                playbackSessionId,
                userId,
                serverId,
                plan,
                queueOrdinal,
                entries.Count,
                complete,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            admissionLock.Release();
        }
    }

    private async Task<PermalinkPlaybackSnapshot> ContinueSessionAsync(
        string root,
        string handle,
        string playbackSessionId,
        Guid userId,
        Guid serverId,
        PermalinkPlaybackPlanStore.FrozenPlaybackPlan plan,
        int queueOrdinal,
        int queueCount,
        bool complete,
        CancellationToken cancellationToken)
    {
        var consumption = await PermalinkPlaybackDocumentStore.ReadAsync<PlaybackConsumedDocument>(
            Path.Combine(root, "consumed.json"),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                consumption.PlaybackSessionId,
                playbackSessionId,
                StringComparison.Ordinal)
            || !consumption.UserId.Equals(userId)
            || !consumption.ServerId.Equals(serverId))
        {
            throw Conflict(
                "playback-lease-consumed",
                "The playback lease was consumed by another session.");
        }

        _activeSessions[handle] = new ActivePlaybackSession(
            playbackSessionId,
            userId,
            serverId);
        var ready = await _plans.VerifyOrdinalAsync(
            root,
            plan,
            queueOrdinal,
            cancellationToken).ConfigureAwait(false);
        await CompleteIfRequestedAsync(
            root,
            consumption,
            queueOrdinal,
            queueCount,
            complete,
            cancellationToken).ConfigureAwait(false);
        return Result(ready, queueCount, playbackSessionId);
    }

    private async Task<PermalinkPlaybackSnapshot> StartSessionAsync(
        string root,
        string handle,
        string playbackSessionId,
        Guid userId,
        Guid serverId,
        PermalinkPlaybackPlanStore.FrozenPlaybackPlan plan,
        int queueOrdinal,
        int queueCount,
        bool complete,
        CancellationToken cancellationToken)
    {
        if (queueOrdinal != 0)
        {
            throw Conflict(
                "playback-ordinal-skipped",
                "The first playback redemption must consume queue ordinal 0.");
        }

        var ready = await _plans.VerifyOrdinalAsync(
            root,
            plan,
            queueOrdinal,
            cancellationToken).ConfigureAwait(false);
        await _documents.PublishAsync(
            Path.Combine(root, "ready.json"),
            ready,
            cancellationToken).ConfigureAwait(false);
        var consumption = new PlaybackConsumedDocument(
            playbackSessionId,
            userId,
            serverId,
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await _documents.PublishAsync(
            Path.Combine(root, "consumed.json"),
            consumption,
            cancellationToken).ConfigureAwait(false);
        _activeSessions[handle] = new ActivePlaybackSession(
            playbackSessionId,
            userId,
            serverId);
        await CompleteIfRequestedAsync(
            root,
            consumption,
            queueOrdinal,
            queueCount,
            complete,
            cancellationToken).ConfigureAwait(false);
        return Result(ready, queueCount, playbackSessionId);
    }

    private async Task CompleteIfRequestedAsync(
        string root,
        PlaybackConsumedDocument consumed,
        int ordinal,
        int queueCount,
        bool complete,
        CancellationToken cancellationToken)
    {
        if (!complete)
        {
            return;
        }

        if (ordinal != queueCount - 1)
        {
            throw Conflict(
                "playback-complete-early",
                "Playback cannot complete before the final frozen queue entry.");
        }

        var path = Path.Combine(root, "completed.json");
        if (File.Exists(path))
        {
            var existing = await PermalinkPlaybackDocumentStore.ReadAsync<PlaybackCompletedDocument>(
                path,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    existing.PlaybackSessionId,
                    consumed.PlaybackSessionId,
                    StringComparison.Ordinal)
                || existing.Ordinal != ordinal)
            {
                throw Conflict(
                    "playback-complete-conflict",
                    "Playback completion belongs to another session or queue entry.");
            }

            return;
        }

        await _documents.PublishAsync(
            path,
            new PlaybackCompletedDocument(
                consumed.PlaybackSessionId,
                ordinal,
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
    }

    private static PermalinkPlaybackSnapshot Result(
        PermalinkPlaybackPlanStore.PlaybackReadyDocument ready,
        int queueCount,
        string playbackSessionId)
    {
        return new PermalinkPlaybackSnapshot(
            ready.ItemId,
            ready.VerifiedPaths[0],
            queueCount,
            playbackSessionId,
            ready.VerifiedPaths);
    }

    private string Root(string handle)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-playback-leases",
            handle);
    }

    private bool IsActiveHandle(string handle)
    {
        return _activeSessions.ContainsKey(handle);
    }

    private SemaphoreSlim GetAdmissionLock(string handle)
    {
        return _recovery.GetAdmissionLock(handle);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    private sealed record PlaybackConsumedDocument(
        string PlaybackSessionId,
        Guid UserId,
        Guid ServerId,
        string CreatedAt);

    private sealed record PlaybackCompletedDocument(
        string PlaybackSessionId,
        int Ordinal,
        string CreatedAt);

    private sealed record ActivePlaybackSession(
        string PlaybackSessionId,
        Guid UserId,
        Guid ServerId);
}
