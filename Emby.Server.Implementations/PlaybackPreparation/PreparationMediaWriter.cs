// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Permalinks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.PlaybackPreparation;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Installs a claimed job's prepared output through the existing guarded writer.</summary>
internal sealed class PreparationMediaWriter
{
    private readonly IPlaybackPreparationService _preparation;
    private readonly IPermalinkMutationCoordinator _mutations;
    private readonly ILibraryManager _library;
    private readonly IServerApplicationHost _appHost;
    private readonly Func<bool> _isIdle;
    private readonly Guid _itemId;
    private readonly string _workspace;
    private string _path;
    private string _identity;
    private bool _mediaInfoRefreshRequired;

    internal sealed record PreparedMediaReplacement(string ItemId, string OriginalPath, string StagedPath, string? DestinationPath);

    public PreparationMediaWriter(
        IPlaybackPreparationService preparation,
        IPermalinkMutationCoordinator mutations,
        ILibraryManager library,
        IServerApplicationHost appHost,
        Func<bool> isIdle,
        PlaybackPreparationRequest claim,
        string workspace)
    {
        _preparation = preparation;
        _mutations = mutations;
        _library = library;
        _appHost = appHost;
        _isIdle = isIdle;
        _itemId = Guid.Parse(claim.ItemId!);
        _path = claim.Path!;
        _identity = claim.MediaSignature!;
        _workspace = workspace;
    }

    public async Task<string> ReplaceAsync(PreparedMediaReplacement replacement, PlaybackPreparationRequest claim, CancellationToken token)
    {
        if (!Guid.TryParse(replacement.ItemId, out var itemId) || !itemId.Equals(_itemId)
            || !string.Equals(replacement.OriginalPath, _path, StringComparison.Ordinal))
        {
            throw Error("replacement-binding-mismatch", "The replacement does not belong to this job's current media record.");
        }

        if (!_isIdle())
        {
            throw Error("playback-active", "Media preparation paused because a viewer is watching. It will retry automatically.");
        }

        if (!string.Equals(PermalinkObjectIdentity.Read(_path), _identity, StringComparison.Ordinal))
        {
            throw Error("media-changed", "The source changed while its prepared output was being encoded.");
        }

        var item = _library.GetItemById<Video>(_itemId);
        if (item is null || !string.Equals(_appHost.ExpandVirtualPath(item.Path), _path, StringComparison.Ordinal))
        {
            throw Error("media-location-changed", "The job's library record moved before its prepared output was installed.");
        }

        ValidateStagedPath(replacement.StagedPath);
        var destination = replacement.DestinationPath;
        if (destination is not null && !string.Equals(destination, _path, StringComparison.Ordinal)
            && (!Path.IsPathFullyQualified(destination)
                || !string.Equals(Path.GetDirectoryName(destination), Path.GetDirectoryName(_path), StringComparison.Ordinal)
                || !string.Equals(Path.GetFileNameWithoutExtension(destination), Path.GetFileNameWithoutExtension(_path), StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("replacement-destination-refused", "A preparation job may only retain its filename or change the same file's container to MP4.");
        }

        await MutateAsync(item, "media", null, replacement.StagedPath, claim, token).ConfigureAwait(false);
        _mediaInfoRefreshRequired = true;
        _identity = ReadInstalledIdentity();
        if (destination is not null && !string.Equals(destination, _path, StringComparison.Ordinal))
        {
            await MutateAsync(item, "path", destination, null, claim, token).ConfigureAwait(false);
            _path = destination;
            _identity = ReadInstalledIdentity();
        }

        return _path;
    }

    public async Task RefreshMediaInfoIfRequiredAsync(CancellationToken token)
    {
        if (!_mediaInfoRefreshRequired)
        {
            return;
        }

        var item = _library.GetItemById<Video>(_itemId);
        if (item is null || !string.Equals(_appHost.ExpandVirtualPath(item.Path), _path, StringComparison.Ordinal))
        {
            throw Error("media-location-changed", "The job's library record moved before its prepared media metadata could be refreshed.");
        }

        if (!File.Exists(_path))
        {
            throw Error("media-unavailable", "The installed media is unavailable for metadata refresh.");
        }

        // The bytes changed, so a previous successful probe cannot establish
        // that the stored codec/stream metadata still describes this file.
        item.IngestState = IngestState.Pending;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, token).ConfigureAwait(false);
        await item.RefreshMetadata(
            new MetadataRefreshOptions(new DirectoryService(BaseItem.FileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false
            },
            token).ConfigureAwait(false);

        if (item.IngestState != IngestState.Complete)
        {
            throw Error("media-metadata-refresh-failed", $"The library could not probe the prepared media at '{_path}' (ingest state: {item.IngestState}).");
        }

        _mediaInfoRefreshRequired = false;
    }

    private void ValidateStagedPath(string stagedPath)
    {
        if (string.IsNullOrEmpty(stagedPath) || !Path.IsPathFullyQualified(stagedPath)
            || string.Equals(stagedPath, _path, StringComparison.OrdinalIgnoreCase))
        {
            throw Error("replacement-staging-refused", "Prepared output must be a separate absolute file.");
        }

        var fullPath = Path.GetFullPath(stagedPath);
        var relative = Path.GetRelativePath(_workspace, fullPath);
        var inWorkspace = !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!inWorkspace && !string.Equals(Path.GetDirectoryName(fullPath), Path.GetDirectoryName(_path), StringComparison.Ordinal))
        {
            throw Error("replacement-staging-refused", "Prepared output must be in this job's workspace or beside its source.");
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(fullPath).Length == 0)
        {
            throw Error("replacement-staging-refused", "Prepared output must be a nonempty regular file, not a directory or symbolic link.");
        }

        if (_library.GetItemList(new InternalItemsQuery
        {
            Path = fullPath,
            Recursive = true,
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false
        }).Any())
        {
            throw Error("replacement-staging-indexed", "Another library record already owns the proposed staging file.");
        }
    }

    private async Task MutateAsync(
        Video item,
        string kind,
        string? destination,
        string? stagedPath,
        PlaybackPreparationRequest claim,
        CancellationToken token)
    {
        var requestId = Guid.Parse(claim.RequestId!);
        await _preparation.RenewAsync(requestId, claim, token).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var operation = RunAsync();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            while (!operation.IsCompleted)
            {
                var interval = timer.WaitForNextTickAsync(token).AsTask();
                if (await Task.WhenAny(operation, interval).ConfigureAwait(false) == operation)
                {
                    break;
                }

                token.ThrowIfCancellationRequested();
                await _preparation.RenewAsync(requestId, claim, token).ConfigureAwait(false);
            }
        }
        catch (Exception leaseFailure)
        {
            // The admitted writer owns durable recovery. Always observe its
            // result even when a renewal or cancellation interrupts the job.
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception mutationFailure)
            {
                throw new AggregateException("Preparation renewal and guarded replacement both failed.", leaseFailure, mutationFailure);
            }

            throw;
        }

        await operation.ConfigureAwait(false);

        async Task RunAsync()
        {
            var prepared = await _mutations.PrepareAsync(
                item,
                new PermalinkMutationPrepareRequest(
                    operationId,
                    item.Id,
                    kind,
                    destination,
                    null,
                    item.Path),
                token).ConfigureAwait(false);
            if (prepared.State != "prepared")
            {
                throw Error("replacement-not-prepared", $"Operation '{operationId}' returned '{prepared.State}' before installation.");
            }

            var result = await _mutations.CommitAsync(operationId, new PermalinkMutationCommitRequest(stagedPath), token).ConfigureAwait(false);
            if (result.State != "committed")
            {
                throw Error("replacement-not-committed", $"Operation '{operationId}' returned '{result.State}'; its journal must settle before retry.");
            }
        }
    }

    private string ReadInstalledIdentity() => PermalinkObjectIdentity.Read(_path)
        ?? throw Error("media-unavailable", "The installed media could not be read after the guarded operation completed.");

    private static PlaybackPreparationException Error(string code, string message) => new(409, code, message);
}
