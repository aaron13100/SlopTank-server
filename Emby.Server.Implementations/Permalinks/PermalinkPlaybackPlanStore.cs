using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Persists frozen playback queues and materializes each immutable entry snapshot.
/// </summary>
internal sealed class PermalinkPlaybackPlanStore
{
    private readonly PermalinkPlaybackDocumentStore _documents;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    public PermalinkPlaybackPlanStore(
        PermalinkPlaybackDocumentStore documents,
        IPermalinkAtomicFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        _documents = documents;
        _fileSystem = fileSystem;
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

    public async Task<PlaybackReadyDocument> MaterializeOrdinalAsync(
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
            await ValidateReadyAsync(existing, entry, ordinal, cancellationToken)
                .ConfigureAwait(false);
            return existing;
        }

        var paths = await MaterializeEntryAsync(
            root,
            entry,
            ordinal,
            cancellationToken).ConfigureAwait(false);
        var ready = new PlaybackReadyDocument(
            ordinal,
            entry.ItemId,
            paths,
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await _documents.PublishAsync(path, ready, cancellationToken).ConfigureAwait(false);
        return ready;
    }

    private async Task<IReadOnlyList<string>> MaterializeEntryAsync(
        string root,
        PlaybackPlanEntry entry,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var snapshotRoot = Path.Combine(
            root,
            "entries",
            ordinal.ToString(CultureInfo.InvariantCulture),
            "snapshots");
        _fileSystem.CreateDirectoryDurable(snapshotRoot);
        var snapshots = new List<string>(entry.Sources.Count);
        foreach (var source in entry.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(
                snapshotRoot,
                source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(source.Path, destination);
            }

            var digest = await DigestFileAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(digest, source.Digest, StringComparison.Ordinal))
            {
                Directory.Delete(snapshotRoot, recursive: true);
                throw Conflict(
                    "playback-source-replaced",
                    $"Playback source '{source.Path}' changed while its immutable snapshot was created.");
            }

            snapshots.Add(destination);
        }

        return snapshots;
    }

    private static async Task ValidateReadyAsync(
        PlaybackReadyDocument ready,
        PlaybackPlanEntry entry,
        int ordinal,
        CancellationToken cancellationToken)
    {
        if (ready.Ordinal != ordinal
            || !ready.ItemId.Equals(entry.ItemId)
            || ready.SnapshotPaths.Count != entry.Sources.Count)
        {
            throw Conflict(
                "playback-ready-mismatch",
                $"Ready state for queue ordinal {ordinal} does not match the frozen plan.");
        }

        for (var index = 0; index < ready.SnapshotPaths.Count; index++)
        {
            var path = ready.SnapshotPaths[index];
            if (!File.Exists(path)
                || !string.Equals(
                    await DigestFileAsync(path, cancellationToken).ConfigureAwait(false),
                    entry.Sources[index].Digest,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "playback-snapshot-missing",
                    $"Ready snapshot '{path}' is missing or no longer matches its frozen digest.");
            }
        }
    }

    internal static async Task<string> DigestFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return "sha256:" + Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    internal sealed record PlaybackSource(
        string Path,
        string RelativePath,
        string Digest);

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
        IReadOnlyList<string> SnapshotPaths,
        string CreatedAt);
}
