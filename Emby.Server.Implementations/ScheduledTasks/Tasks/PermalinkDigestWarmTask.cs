using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Permalinks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Computes permalink content digests ahead of time, so that opening a watch
/// link never waits on one.
///
/// A permalink proves which bytes it points at with a full-content SHA-256.
/// That digest is durable once computed, but computing it costs a full read:
/// measured on production 2026-09-02, 397.05s for a 10.53 GB film and 8.79s for
/// a 0.11 GB episode, roughly 26 MB/s. Paid on the user's click it is the
/// difference between a video starting and a person giving up; the same click
/// measured 229.07s to first frame on a cold 10.53 GB film against 7.29s warm.
///
/// So the read moves off the click and onto a quiet hour. Nothing here makes
/// the hash cheaper: it makes it already done. New media added during the day
/// is still hashed on first play, which is the one case this cannot pre-empt
/// and the reason the cost is worth removing everywhere else.
///
/// The pass yields the moment anyone starts watching. It reads every media file
/// on a two-core host with external storage, and a background job that competes
/// with playback has simply moved the problem rather than solved it.
/// </summary>
public sealed class PermalinkDigestWarmTask : IScheduledTask
{
    private const int PageSize = 100;

    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie];

    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ISessionManager _sessionManager;
    private readonly PermalinkEvidence _evidence;
    private readonly ILogger<PermalinkDigestWarmTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkDigestWarmTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="sessionManager">The session manager, used to yield to viewers.</param>
    /// <param name="evidence">Computes and records the content digest.</param>
    /// <param name="logger">The logger.</param>
    public PermalinkDigestWarmTask(
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        ISessionManager sessionManager,
        PermalinkEvidence evidence,
        ILogger<PermalinkDigestWarmTask> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _sessionManager = sessionManager;
        _evidence = evidence;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Precompute Permalink Digests";

    /// <inheritdoc/>
    public string Description =>
        "Reads each media file once so that opening a watch link never waits on a content hash.";

    /// <inheritdoc/>
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc/>
    public string Key => "PermalinkDigestWarm";

    /// <summary>
    /// Runs nightly rather than at startup.
    ///
    /// The digests are durable, so a restart no longer loses them and there is
    /// nothing to rebuild on boot. Nightly exists to pick up media added during
    /// the day, at the hour least likely to collide with someone watching.
    /// </summary>
    /// <returns>The default triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(0);

        if (!_evidence.PersistsContentDigests)
        {
            _logger.LogInformation(
                "Permalink authority is not configured, so content digests cannot be stored; nothing to precompute");
            progress.Report(100);
            return;
        }

        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true,
            Limit = PageSize
        };

        var total = _libraryManager.GetCount(query);
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        var examined = 0;
        var warmed = 0;
        var startIndex = 0;

        while (startIndex < total)
        {
            query.StartIndex = startIndex;
            var page = _libraryManager.GetItemList(query);

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsAnyoneWatching())
                {
                    _logger.LogInformation(
                        "Stopping the permalink digest pass after {Examined} of {Total} items because playback started; it resumes on the next run",
                        examined,
                        total);
                    progress.Report(100);
                    return;
                }

                examined++;
                progress.Report(100d * examined / total);

                if (item is not Video video || !item.IsFileProtocol || !File.Exists(item.Path))
                {
                    continue;
                }

                if (await WarmAsync(video, cancellationToken).ConfigureAwait(false))
                {
                    warmed++;
                }
            }

            startIndex += PageSize;
        }

        _logger.LogInformation(
            "Permalink digest pass finished: {Warmed} newly hashed of {Total} items examined",
            warmed,
            total);
        progress.Report(100);
    }

    /// <summary>
    /// Computes and records one item's digest, returning whether the file had
    /// to be read.
    ///
    /// A per-item failure is logged and skipped rather than ending the pass:
    /// one unreadable or ineligible file must not stop every later item from
    /// being warmed, and the item will simply be hashed on first play as it is
    /// today.
    /// </summary>
    private async Task<bool> WarmAsync(Video video, CancellationToken cancellationToken)
    {
        if (_evidence.IsContentDigestKnown(video.Path))
        {
            return false;
        }

        try
        {
            await _evidence.ComputeContentItemAsync(video, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PermalinkException exception)
        {
            _logger.LogDebug(
                exception,
                "Skipped {ItemName} at {Path}: it has no content shape a permalink digest applies to",
                video.Name,
                video.Path);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Could not read {ItemName} at {Path} to precompute its permalink digest; it will be hashed on first play instead",
                video.Name,
                video.Path);
            return false;
        }
    }

    /// <summary>
    /// Returns whether any session is currently playing something.
    /// </summary>
    private bool IsAnyoneWatching()
    {
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.NowPlayingItem is not null)
            {
                return true;
            }
        }

        return false;
    }
}
