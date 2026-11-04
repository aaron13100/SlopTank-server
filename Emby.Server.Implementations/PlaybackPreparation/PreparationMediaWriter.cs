// SlopTank modification notice: added or changed by SlopTank on 2026-11-03, 2026-11-04.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Installs a claimed job's prepared output through the existing guarded writer.</summary>
internal sealed class PreparationMediaWriter
{
    private readonly IPlaybackPreparationService _preparation;
    private readonly ILibraryManager _library;
    private readonly IServerApplicationHost _appHost;
    private readonly Func<bool> _isIdle;
    private readonly Guid _itemId;
    private readonly string _workspace;
    private readonly string _retiredDirectory;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private string _path;
    private string _identity;
    private bool _mediaInfoRefreshRequired;

    public PreparationMediaWriter(
        IPlaybackPreparationService preparation,
        ILibraryManager library,
        IServerApplicationHost appHost,
        string retiredDirectory,
        TimeProvider clock,
        ILogger logger,
        Func<bool> isIdle,
        PlaybackPreparationRequest claim,
        string workspace)
    {
        _preparation = preparation;
        _library = library;
        _appHost = appHost;
        _clock = clock;
        _logger = logger;
        _isIdle = isIdle;
        _itemId = Guid.Parse(claim.ItemId!);
        _path = claim.Path!;
        _identity = claim.MediaSignature!;
        _workspace = workspace;
        _retiredDirectory = retiredDirectory;
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

        if (!string.Equals(MediaFileFingerprint.Read(_path), _identity, StringComparison.Ordinal))
        {
            throw Error("media-changed", "The source changed while its prepared output was being encoded.");
        }

        var item = _library.GetItemById<Video>(_itemId);
        if (item is null || !string.Equals(_appHost.ExpandVirtualPath(item.Path), _path, StringComparison.Ordinal))
        {
            throw Error("media-location-changed", "The job's library record moved before its prepared output was installed.");
        }

        ValidateStagedPath(replacement.StagedPath);
        var destination = replacement.DestinationPath is null ? _path : Path.GetFullPath(replacement.DestinationPath);
        if (!PathsEqual(destination, _path)
            && (!Path.IsPathFullyQualified(destination)
                || !string.Equals(Path.GetDirectoryName(destination), Path.GetDirectoryName(_path), StringComparison.Ordinal)
                || !string.Equals(Path.GetFileNameWithoutExtension(destination), Path.GetFileNameWithoutExtension(_path), StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("replacement-destination-refused", "A preparation job may only retain its filename or change the same file's container to MP4.");
        }

        var samePath = PathsEqual(destination, _path);
        if (!samePath && File.Exists(destination))
        {
            throw Error("replacement-destination-occupied", "The new media path already exists and cannot be overwritten.");
        }

        EnsureRetiredDirectoryIsOutsideLibrary();
        var stagedForPublish = await CopyToDestinationTempAsync(replacement.StagedPath, destination, token).ConfigureAwait(false);
        try
        {
            await PreserveOriginalAsync(_path, _identity, token).ConfigureAwait(false);
            await _preparation.RenewAsync(Guid.Parse(claim.RequestId!), claim, token).ConfigureAwait(false);
            if (!_isIdle())
            {
                throw Error("playback-active", "Media preparation paused because a viewer is watching. It will retry automatically.");
            }

            if (!string.Equals(MediaFileFingerprint.Read(_path), _identity, StringComparison.Ordinal))
            {
                throw Error("media-changed", "The source changed while its prepared output was being encoded.");
            }

            var currentItem = _library.GetItemById<Video>(_itemId);
            if (currentItem is null || !PathsEqual(_appHost.ExpandVirtualPath(currentItem.Path), _path))
            {
                throw Error("media-location-changed", "The job's library record moved before its prepared output was installed.");
            }

            File.Move(stagedForPublish, destination, samePath);
            currentItem.Path = destination;
            currentItem.IngestState = IngestState.Pending;
            currentItem.PlaybackReadiness = PlaybackReadiness.Pending;
            currentItem.PlaybackReadyObjectIdentity = null;
            currentItem.PlaybackReadyAlgorithmVersion = null;
            await currentItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, token).ConfigureAwait(false);

            _path = destination;
            _identity = ReadInstalledIdentity();
            _mediaInfoRefreshRequired = true;

            if (!samePath)
            {
                File.Delete(replacement.OriginalPath);
            }
        }
        finally
        {
            if (File.Exists(stagedForPublish))
            {
                File.Delete(stagedForPublish);
            }
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

    private async Task<string> PreserveOriginalAsync(string sourcePath, string expectedFingerprint, CancellationToken token)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw Error("media-unavailable", "The original media file is missing from its library path.");
        }

        Directory.CreateDirectory(_retiredDirectory);
        try
        {
            if (new DriveInfo(_retiredDirectory).AvailableFreeSpace < source.Length)
            {
                throw Error("retirement-capacity-insufficient", $"The retirement directory does not have {source.Length} bytes available to preserve the original.");
            }
        }
        catch (PlaybackPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw Error("retirement-capacity-unavailable", $"Could not inspect available space in '{_retiredDirectory}': {exception.Message}");
        }

        var backupPath = Path.Combine(_retiredDirectory, $"{Guid.NewGuid():N}-{Path.GetFileName(sourcePath)}");
        var temporaryPath = backupPath + ".partial";
        try
        {
            await CopyVerifiedAsync(sourcePath, temporaryPath, source.Length, token).ConfigureAwait(false);

            if (!string.Equals(MediaFileFingerprint.Read(sourcePath), expectedFingerprint, StringComparison.Ordinal))
            {
                throw Error("media-changed", "The original changed while it was being copied to retirement storage.");
            }

            File.Move(temporaryPath, backupPath);
            await AppendRetirementLedgerRecordAsync(backupPath, sourcePath, source.Length).ConfigureAwait(false);
            return backupPath;
        }
        catch (Exception exception)
        {
            CleanupPartial(temporaryPath);

            if (exception is PlaybackPreparationException or OperationCanceledException)
            {
                throw;
            }

            throw Error("retirement-backup-failed", $"Could not preserve the original media at '{sourcePath}': {exception.Message}");
        }
    }

    private async Task AppendRetirementLedgerRecordAsync(string slotPath, string sourcePath, long sizeBytes)
    {
        var slotName = Path.GetFileName(slotPath);
        var ledgerPath = Path.Combine(_retiredDirectory, ".retirement-ledger.jsonl");
        var record = new
        {
            kind = "retirement",
            version = 1,
            slot_name = slotName,
            source_path = sourcePath,
            retired_at = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d,
            size_bytes = sizeBytes,
            retired_at_source = "observed"
        };

        try
        {
            var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
            await using var stream = new FileStream(
                ledgerPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true);
            await stream.WriteAsync(line).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            await Task.Run(() => stream.Flush(flushToDisk: true)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not append a retirement ledger record for {SlotName} in {RetiredDirectory}; the replacement is unaffected, but this file's undo window can no longer be dated precisely",
                slotName,
                _retiredDirectory);
        }
    }

    private async Task<string> CopyToDestinationTempAsync(string stagedPath, string destinationPath, CancellationToken token)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        var stagedLength = new FileInfo(stagedPath).Length;
        try
        {
            await CopyVerifiedAsync(stagedPath, temporaryPath, stagedLength, token).ConfigureAwait(false);

            return temporaryPath;
        }
        catch (Exception exception)
        {
            CleanupPartial(temporaryPath);

            if (exception is OperationCanceledException or PlaybackPreparationException)
            {
                throw;
            }

            throw Error("replacement-staging-failed", $"Could not stage prepared media beside its destination: {exception.Message}");
        }
    }

    private static async Task CopyVerifiedAsync(string sourcePath, string destinationPath, long expectedLength, CancellationToken token)
    {
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
        await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            // FlushAsync only drains buffered writes. Retirement requires the
            // durable flush, which has a synchronous API, off the request thread.
            await Task.Run(() => output.Flush(flushToDisk: true), token).ConfigureAwait(false);
        }

        if (new FileInfo(destinationPath).Length != expectedLength)
        {
            throw new IOException($"Copied media at '{destinationPath}' does not match the expected {expectedLength} bytes.");
        }
    }

    private static void CleanupPartial(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void EnsureRetiredDirectoryIsOutsideLibrary()
    {
        var retiredPath = Path.GetFullPath(_retiredDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var isInsideLibrary = _library.GetVirtualFolders()
            .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
            .Where(location => !string.IsNullOrWhiteSpace(location) && Path.IsPathFullyQualified(location))
            .Select(Path.GetFullPath)
            .Any(location =>
            {
                var relative = Path.GetRelativePath(location, retiredPath);
                return !Path.IsPathRooted(relative)
                    && (string.Equals(relative, ".", comparison)
                        || (relative != ".."
                            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison)));
            });
        if (isInsideLibrary)
        {
            throw Error("retirement-directory-inside-library", "The media retirement directory must be outside every configured library location.");
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
    }

    private string ReadInstalledIdentity() => MediaFileFingerprint.Read(_path)
        ?? throw Error("media-unavailable", "The installed media could not be read after replacement.");

    private static PlaybackPreparationException Error(string code, string message) => new(409, code, message);

    internal sealed record PreparedMediaReplacement(string ItemId, string OriginalPath, string StagedPath, string? DestinationPath);
}
