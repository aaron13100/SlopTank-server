using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Persists frozen playback queues and verifies each entry's media in place.
/// </summary>
internal sealed class PermalinkPlaybackPlanStore
{
    private readonly PermalinkPlaybackDocumentStore _documents;
    private readonly TimeProvider _timeProvider;

    public PermalinkPlaybackPlanStore(
        PermalinkPlaybackDocumentStore documents,
        TimeProvider timeProvider)
    {
        _documents = documents;
        _timeProvider = timeProvider;
    }

    public async Task<FrozenPlaybackPlan> ReadOrPublishAsync(
        string root,
        string handle,
        Guid userId,
        Guid serverId,
        IReadOnlyList<PlaybackPlanEntry> entries,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "plan.json");
        if (!File.Exists(path))
        {
            var created = new FrozenPlaybackPlan(
                handle,
                userId,
                serverId,
                entries,
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await _documents.PublishAsync(path, created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        var plan = await PermalinkPlaybackDocumentStore.ReadAsync<FrozenPlaybackPlan>(
            path,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(plan.Handle, handle, StringComparison.Ordinal)
            || !plan.UserId.Equals(userId)
            || !plan.ServerId.Equals(serverId)
            || !CanonicalJson.Serialize(plan.Entries).Span.SequenceEqual(
                CanonicalJson.Serialize(entries).Span))
        {
            throw Conflict(
                "playback-plan-changed",
                "The frozen playback plan no longer matches the verified queue.");
        }

        return plan;
    }

    public async Task<PlaybackReadyDocument> VerifyOrdinalAsync(
        string root,
        FrozenPlaybackPlan plan,
        int ordinal,
        CancellationToken cancellationToken)
    {
        if (ordinal > 0
            && !File.Exists(Path.Combine(
                root,
                "entries",
                (ordinal - 1).ToString(CultureInfo.InvariantCulture),
                "ready.json")))
        {
            throw Conflict(
                "playback-ordinal-skipped",
                $"Queue ordinal {ordinal - 1} must be ready before ordinal {ordinal}.");
        }

        var entry = plan.Entries[ordinal];
        var path = Path.Combine(
            root,
            "entries",
            ordinal.ToString(CultureInfo.InvariantCulture),
            "ready.json");
        if (File.Exists(path))
        {
            var existing = await PermalinkPlaybackDocumentStore.ReadAsync<PlaybackReadyDocument>(
                path,
                cancellationToken).ConfigureAwait(false);
            ValidateReady(existing, entry, ordinal);
            return existing;
        }

        var ready = new PlaybackReadyDocument(
            ordinal,
            entry.ItemId,
            VerifyEntry(entry),
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await _documents.PublishAsync(path, ready, cancellationToken).ConfigureAwait(false);
        return ready;
    }

    /// <summary>
    /// Confirms every planned source is still the exact filesystem object the
    /// plan froze, and returns those verified source paths.
    /// </summary>
    /// <param name="entry">The frozen queue entry being readied.</param>
    /// <returns>The verified source paths, in frozen order.</returns>
    /// <remarks>
    /// This deliberately performs no content read. Playback previously copied
    /// every source into the lease root and hashed it twice, which cost three
    /// full passes over the media on the request path and delivered nothing:
    /// streaming reads the library file, never the copy. Identity comparison
    /// detects the replacement this conflict exists for at constant cost.
    /// </remarks>
    private static IReadOnlyList<string> VerifyEntry(PlaybackPlanEntry entry)
    {
        var verified = new List<string>(entry.Sources.Count);
        foreach (var source in entry.Sources)
        {
            if (!string.Equals(
                    PermalinkObjectIdentity.Read(source.Path),
                    source.Identity,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "playback-source-replaced",
                    $"Playback source '{source.Path}' changed after its playback plan was frozen.");
            }

            verified.Add(source.Path);
        }

        return verified;
    }

    private static void ValidateReady(
        PlaybackReadyDocument ready,
        PlaybackPlanEntry entry,
        int ordinal)
    {
        if (ready.Ordinal != ordinal
            || !ready.ItemId.Equals(entry.ItemId)
            || ready.VerifiedPaths.Count != entry.Sources.Count)
        {
            throw Conflict(
                "playback-ready-mismatch",
                $"Ready state for queue ordinal {ordinal} does not match the frozen plan.");
        }

        for (var index = 0; index < ready.VerifiedPaths.Count; index++)
        {
            var path = ready.VerifiedPaths[index];
            if (!string.Equals(path, entry.Sources[index].Path, StringComparison.Ordinal)
                || !string.Equals(
                    PermalinkObjectIdentity.Read(path),
                    entry.Sources[index].Identity,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "playback-snapshot-missing",
                    $"Verified source '{path}' is missing or no longer matches its frozen identity.");
            }
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    internal sealed record PlaybackSource(
        string Path,
        string RelativePath,
        string Identity);

    internal sealed record PlaybackPlanEntry(
        Guid ItemId,
        IReadOnlyList<PlaybackSource> Sources);

    internal sealed record FrozenPlaybackPlan(
        string Handle,
        Guid UserId,
        Guid ServerId,
        IReadOnlyList<PlaybackPlanEntry> Entries,
        string CreatedAt);

    internal sealed record PlaybackReadyDocument(
        int Ordinal,
        Guid ItemId,
        IReadOnlyList<string> VerifiedPaths,
        string CreatedAt);
}
