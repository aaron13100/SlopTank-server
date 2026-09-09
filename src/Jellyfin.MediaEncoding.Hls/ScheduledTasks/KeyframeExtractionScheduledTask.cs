// SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.MediaEncoding.Hls.Extractors;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.ScheduledTasks;

/// <inheritdoc />
public class KeyframeExtractionScheduledTask : IScheduledTask
{
    private const int Pagesize = 1000;

    private readonly ILocalizationManager _localizationManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IKeyframeExtractor[] _keyframeExtractors;
    private readonly ILogger<KeyframeExtractionScheduledTask> _logger;
    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie];

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyframeExtractionScheduledTask"/> class.
    /// </summary>
    /// <param name="localizationManager">An instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="libraryManager">An instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="keyframeExtractors">The keyframe extractors.</param>
    /// <param name="logger">The logger.</param>
    public KeyframeExtractionScheduledTask(
        ILocalizationManager localizationManager,
        ILibraryManager libraryManager,
        IEnumerable<IKeyframeExtractor> keyframeExtractors,
        ILogger<KeyframeExtractionScheduledTask> logger)
    {
        _localizationManager = localizationManager;
        _libraryManager = libraryManager;
        _keyframeExtractors = keyframeExtractors.OrderByDescending(e => e.IsMetadataBased).ToArray();
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => _localizationManager.GetLocalizedString("TaskKeyframeExtractor");

    /// <inheritdoc />
    public string Key => "KeyframeExtraction";

    /// <inheritdoc />
    public string Description => _localizationManager.GetLocalizedString("TaskKeyframeExtractorDescription");

    /// <inheritdoc />
    public string Category => _localizationManager.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            IncludeOwnedItems = true,
            Limit = Pagesize
        };

        var numberOfVideos = _libraryManager.GetCount(query);

        var startIndex = 0;
        var numComplete = 0;
        var failureCount = 0;

        while (startIndex < numberOfVideos)
        {
            query.StartIndex = startIndex;

            var videos = _libraryManager.GetItemList(query);
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only local files supported
                var path = video.Path;
                if (File.Exists(path))
                {
                    try
                    {
                        var extracted = false;
                        var extractorThrew = false;
                        foreach (var extractor in _keyframeExtractors)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // The cache decorator will make sure to save the keyframes
                            try
                            {
                                var succeeded = extractor.TryExtractKeyframes(video.Id, path, out _);
                                cancellationToken.ThrowIfCancellationRequested();
                                if (succeeded)
                                {
                                    extracted = true;
                                    break;
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                extractorThrew = true;
                                _logger.LogError(
                                    exception,
                                    "Keyframe extraction failed for item {ItemId} at {FilePath}",
                                    video.Id,
                                    path);
                            }
                        }

                        if (!extracted)
                        {
                            failureCount++;
                            if (!extractorThrew)
                            {
                                _logger.LogWarning(
                                    "No keyframe extractor succeeded for item {ItemId} at {FilePath}",
                                    video.Id,
                                    path);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failureCount++;
                        _logger.LogError(
                            exception,
                            "Keyframe extraction failed for item {ItemId} at {FilePath}",
                            video.Id,
                            path);
                    }
                }

                // Update progress
                numComplete++;
                double percent = (double)numComplete / numberOfVideos;
                progress.Report(100 * percent);
            }

            startIndex += Pagesize;
        }

        progress.Report(100);
        if (failureCount > 0)
        {
            _logger.LogWarning(
                "Keyframe extraction completed with {FailureCount} item failure(s)",
                failureCount);
            throw new InvalidOperationException(
                $"Keyframe extraction completed with {failureCount} item failure(s).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
