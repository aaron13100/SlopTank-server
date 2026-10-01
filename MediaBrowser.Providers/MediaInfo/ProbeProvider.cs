// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo
{
    /// <summary>
    /// The probe provider.
    /// </summary>
    public class ProbeProvider : ICustomMetadataProvider<Episode>,
        ICustomMetadataProvider<MusicVideo>,
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Trailer>,
        ICustomMetadataProvider<Video>,
        ICustomMetadataProvider<Audio>,
        ICustomMetadataProvider<AudioBook>,
        IHasOrder,
        IForcedProvider,
        IPreRefreshProvider,
        IHasItemChangeMonitor
    {
        private readonly ILogger<ProbeProvider> _logger;
        private readonly AudioResolver _audioResolver;
        private readonly SubtitleResolver _subtitleResolver;
        private readonly LyricResolver _lyricResolver;
        private readonly FFProbeVideoInfo _videoProber;
        private readonly AudioFileProber _audioProber;
        private readonly Task<ItemUpdateType> _cachedTask = Task.FromResult(ItemUpdateType.None);

        /// <summary>
        /// Initializes a new instance of the <see cref="ProbeProvider"/> class.
        /// </summary>
        /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
        /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
        /// <param name="blurayExaminer">Instance of the <see cref="IBlurayExaminer"/> interface.</param>
        /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
        /// <param name="chapterManager">Instance of the <see cref="IChapterManager"/> interface.</param>
        /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        /// <param name="subtitleManager">Instance of the <see cref="ISubtitleManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/>.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="namingOptions">The <see cref="NamingOptions"/>.</param>
        /// <param name="lyricManager">Instance of the <see cref="ILyricManager"/> interface.</param>
        /// <param name="mediaAttachmentRepository">Instance of the <see cref="IMediaAttachmentRepository"/> interface.</param>
        /// <param name="mediaStreamRepository">Instance of the <see cref="IMediaStreamRepository"/> interface.</param>
        public ProbeProvider(
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            IBlurayExaminer blurayExaminer,
            ILocalizationManager localization,
            IChapterManager chapterManager,
            IServerConfigurationManager config,
            ISubtitleManager subtitleManager,
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            ILoggerFactory loggerFactory,
            NamingOptions namingOptions,
            ILyricManager lyricManager,
            IMediaAttachmentRepository mediaAttachmentRepository,
            IMediaStreamRepository mediaStreamRepository)
        {
            _logger = loggerFactory.CreateLogger<ProbeProvider>();
            _audioResolver = new AudioResolver(loggerFactory.CreateLogger<AudioResolver>(), localization, mediaEncoder, fileSystem, namingOptions);
            _subtitleResolver = new SubtitleResolver(loggerFactory.CreateLogger<SubtitleResolver>(), localization, mediaEncoder, fileSystem, namingOptions);
            _lyricResolver = new LyricResolver(loggerFactory.CreateLogger<LyricResolver>(), localization, mediaEncoder, fileSystem, namingOptions);

            _videoProber = new FFProbeVideoInfo(
                loggerFactory.CreateLogger<FFProbeVideoInfo>(),
                mediaSourceManager,
                mediaEncoder,
                blurayExaminer,
                localization,
                chapterManager,
                config,
                subtitleManager,
                libraryManager,
                _audioResolver,
                _subtitleResolver,
                mediaAttachmentRepository,
                mediaStreamRepository);

            _audioProber = new AudioFileProber(
                loggerFactory.CreateLogger<AudioFileProber>(),
                mediaSourceManager,
                mediaEncoder,
                libraryManager,
                _lyricResolver,
                lyricManager,
                mediaStreamRepository,
                chapterManager);
        }

        /// <inheritdoc />
        public string Name => "Probe Provider";

        /// <inheritdoc />
        public int Order => 100;

        /// <summary>
        /// Gets a value indicating whether this provider probes the given item at all. This is the single
        /// source of truth for "this item's media is probed after it is created": library creation uses it
        /// to mark such items <see cref="IngestState.Pending"/> until the probe has run.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns><c>true</c> when a file-backed, non-virtual, probe-eligible item.</returns>
        public static bool Supports(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item is not (Video or Audio))
            {
                return false;
            }

            if (item is Video video && (video.IsPlaceHolder || !video.IsCompleteMedia))
            {
                return false;
            }

            return !item.IsVirtualItem
                && item.LocationType == LocationType.FileSystem
                && item.IsFileProtocol;
        }

        /// <summary>
        /// Records a failed probe on the item. This is the single place the failure transition lives:
        /// <see cref="IngestState.Pending"/> becomes <see cref="IngestState.ProbeFailed"/> (hidden, retried on
        /// the next refresh) and ProbeFailed stays ProbeFailed. An item that is already
        /// <see cref="IngestState.Complete"/> stays Complete, because its previously probed streams are still
        /// valid and a transient probe failure (contended volume, load) must not hide a working item.
        /// </summary>
        /// <param name="item">The item whose probe failed.</param>
        /// <param name="logger">The logger of the calling prober.</param>
        /// <param name="exception">The failure, or <c>null</c> when there is no exception (no playable files).</param>
        /// <param name="reason">Short description used when there is no exception.</param>
        public static void MarkProbeFailed(BaseItem item, ILogger logger, Exception exception, string reason = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(logger);

            if (item.IngestState == IngestState.Complete)
            {
                logger.LogWarning(
                    exception,
                    "Probe failed for {Path} ({Reason}); the item is already Complete, so it stays visible with its previously probed streams and is retried on the next refresh",
                    item.Path,
                    reason ?? exception?.Message);
                return;
            }

            item.IngestState = IngestState.ProbeFailed;
            logger.LogError(
                exception,
                "Probe failed for {Path} ({Reason}); item stays hidden (IngestState ProbeFailed) and is retried on the next refresh",
                item.Path,
                reason ?? exception?.Message);
        }

        /// <inheritdoc />
        public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IngestState != IngestState.Complete)
            {
                _logger.LogDebug("Refreshing {ItemPath} because its media has not been probed (IngestState {State}).", item.Path, item.IngestState);
                return true;
            }

            var video = item as Video;
            if (video is null || video.VideoType == VideoType.VideoFile || video.VideoType == VideoType.Iso)
            {
                var path = item.Path;

                if (!string.IsNullOrWhiteSpace(path) && item.IsFileProtocol)
                {
                    var file = directoryService.GetFile(path);
                    if (file is not null && item.HasChanged(file.LastWriteTimeUtc))
                    {
                        _logger.LogDebug("Refreshing {ItemPath} due to file system modification.", path);
                        return true;
                    }
                }
            }

            if (video is not null
                && item.SupportsLocalMetadata
                && !video.IsPlaceHolder)
            {
                var externalFiles = new HashSet<string>(_subtitleResolver.GetExternalFiles(video, directoryService, false).Select(info => info.Path), StringComparer.OrdinalIgnoreCase);
                if (!new HashSet<string>(video.SubtitleFiles, StringComparer.Ordinal).SetEquals(externalFiles))
                {
                    _logger.LogDebug("Refreshing {ItemPath} due to external subtitles change.", item.Path);
                    return true;
                }

                externalFiles = new HashSet<string>(_audioResolver.GetExternalFiles(video, directoryService, false).Select(info => info.Path), StringComparer.OrdinalIgnoreCase);
                if (!new HashSet<string>(video.AudioFiles, StringComparer.Ordinal).SetEquals(externalFiles))
                {
                    _logger.LogDebug("Refreshing {ItemPath} due to external audio change.", item.Path);
                    return true;
                }
            }

            if (item is Audio audio
                && item.SupportsLocalMetadata)
            {
                var externalFiles = new HashSet<string>(_lyricResolver.GetExternalFiles(audio, directoryService, false).Select(info => info.Path), StringComparer.OrdinalIgnoreCase);
                if (!new HashSet<string>(audio.LyricFiles, StringComparer.Ordinal).SetEquals(externalFiles))
                {
                    _logger.LogDebug("Refreshing {ItemPath} due to external lyrics change.", item.Path);
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchVideoInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(MusicVideo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchVideoInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchVideoInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(Trailer item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchVideoInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchVideoInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(Audio item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchAudioInfo(item, options, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(AudioBook item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return FetchAudioInfo(item, options, cancellationToken);
        }

        /// <summary>
        /// Fetches video information for an item.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="options">The <see cref="MetadataRefreshOptions"/>.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <typeparam name="T">The type of item to resolve.</typeparam>
        /// <returns>A <see cref="Task"/> fetching the <see cref="ItemUpdateType"/> for an item.</returns>
        public Task<ItemUpdateType> FetchVideoInfo<T>(T item, MetadataRefreshOptions options, CancellationToken cancellationToken)
            where T : Video
        {
            if (item.IsPlaceHolder)
            {
                return _cachedTask;
            }

            if (!item.IsCompleteMedia)
            {
                return _cachedTask;
            }

            if (item.IsVirtualItem)
            {
                return _cachedTask;
            }

            if (!options.EnableRemoteContentProbe && !item.IsFileProtocol)
            {
                return _cachedTask;
            }

            if (item.IsShortcut)
            {
                FetchShortcutInfo(item);
            }

            return _videoProber.ProbeVideo(item, options, cancellationToken);
        }

        private string NormalizeStrmLine(string line)
        {
            return line.Replace("\t", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private void FetchShortcutInfo(BaseItem item)
        {
            var shortcutPath = File.ReadAllLines(item.Path)
                .Select(NormalizeStrmLine)
                .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i) && !i.StartsWith('#'));

            if (string.IsNullOrWhiteSpace(shortcutPath))
            {
                return;
            }

            // Only allow remote URLs in .strm files to prevent local file access
            if (Uri.TryCreate(shortcutPath, UriKind.Absolute, out var uri)
                && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "rtp", StringComparison.OrdinalIgnoreCase)))
            {
                item.ShortcutPath = shortcutPath;
            }
            else
            {
                _logger.LogWarning("Ignoring invalid or non-remote .strm path in {File}: {Path}", item.Path, shortcutPath);
            }
        }

        /// <summary>
        /// Fetches audio information for an item.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="options">The <see cref="MetadataRefreshOptions"/>.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <typeparam name="T">The type of item to resolve.</typeparam>
        /// <returns>A <see cref="Task"/> fetching the <see cref="ItemUpdateType"/> for an item.</returns>
        public Task<ItemUpdateType> FetchAudioInfo<T>(T item, MetadataRefreshOptions options, CancellationToken cancellationToken)
            where T : Audio
        {
            if (item.IsVirtualItem)
            {
                return _cachedTask;
            }

            if (!options.EnableRemoteContentProbe && !item.IsFileProtocol)
            {
                return _cachedTask;
            }

            if (item.IsShortcut)
            {
                FetchShortcutInfo(item);
            }

            return _audioProber.Probe(item, options, cancellationToken);
        }
    }
}
