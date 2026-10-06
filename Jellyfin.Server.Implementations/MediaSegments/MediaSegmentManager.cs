// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.MediaSegments;

/// <summary>
/// Manages media segments retrieval and storage.
/// </summary>
public class MediaSegmentManager : IMediaSegmentManager
{
    private readonly ILogger<MediaSegmentManager> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IMediaSegmentProvider[] _segmentProviders;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentManager"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="dbProvider">EFCore Database factory.</param>
    /// <param name="segmentProviders">List of all media segment providers.</param>
    public MediaSegmentManager(
        ILogger<MediaSegmentManager> logger,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IEnumerable<IMediaSegmentProvider> segmentProviders)
    {
        _logger = logger;
        _dbProvider = dbProvider;

        _segmentProviders = segmentProviders
            .OrderBy(i => i is IHasOrder hasOrder ? hasOrder.Order : 0)
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken)
    {
        var providers = _segmentProviders
            .Where(e => !libraryOptions.DisabledMediaSegmentProviders.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(i =>
                {
                    var index = libraryOptions.MediaSegmentProviderOrder.IndexOf(i.Name);
                    return index == -1 ? int.MaxValue : index;
                })
            .ToList();

        if (providers.Count == 0)
        {
            _logger.LogDebug("Skipping media segment extraction as no providers are enabled for {MediaPath}", baseItem.Path);
            return;
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            _logger.LogDebug("Start media segment extraction for {MediaPath} with {CountProviders} providers enabled", baseItem.Path, providers.Count);

            if (forceOverwrite)
            {
                // delete all existing media segments if forceOverwrite is set.
                await db.MediaSegments.Where(e => e.ItemId.Equals(baseItem.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await provider.Supports(baseItem).ConfigureAwait(false))
                {
                    _logger.LogDebug("Media Segment provider {ProviderName} does not support item with path {MediaPath}", provider.Name, baseItem.Path);
                    continue;
                }

                IQueryable<MediaSegment> existingSegments;
                if (forceOverwrite)
                {
                    existingSegments = Array.Empty<MediaSegment>().AsQueryable();
                }
                else
                {
                    existingSegments = db.MediaSegments.Where(e => e.ItemId.Equals(baseItem.Id) && e.SegmentProviderId == GetProviderId(provider.Name));
                }

                var requestItem = new MediaSegmentGenerationRequest()
                {
                    ItemId = baseItem.Id,
                    ExistingSegments = existingSegments.Select(e => Map(e)).ToArray()
                };

                try
                {
                    var segments = await provider.GetMediaSegments(requestItem, cancellationToken)
                        .ConfigureAwait(false);

                    if (!forceOverwrite)
                    {
                        var existingSegmentsList = existingSegments.ToArray(); // Cannot use requestItem's list, as the provider might tamper with its items.
                        if (segments.Count == requestItem.ExistingSegments.Count && segments.All(e => existingSegmentsList.Any(f =>
                        {
                            return
                                e.StartTicks == f.StartTicks &&
                                e.EndTicks == f.EndTicks &&
                                e.Type == f.Type;
                        })))
                        {
                            _logger.LogDebug("Media Segment provider {ProviderName} did not modify any segments for {MediaPath}", provider.Name, baseItem.Path);
                            continue;
                        }

                        // delete existing media segments that were re-generated.
                        await existingSegments.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (segments.Count == 0 && !requestItem.ExistingSegments.Any())
                    {
                        _logger.LogDebug("Media Segment provider {ProviderName} did not find any segments for {MediaPath}", provider.Name, baseItem.Path);
                        continue;
                    }
                    else if (segments.Count == 0 && requestItem.ExistingSegments.Any())
                    {
                        _logger.LogDebug("Media Segment provider {ProviderName} deleted all segments for {MediaPath}", provider.Name, baseItem.Path);
                        continue;
                    }

                    _logger.LogInformation("Media Segment provider {ProviderName} found {CountSegments} for {MediaPath}", provider.Name, segments.Count, baseItem.Path);
                    var providerId = GetProviderId(provider.Name);
                    foreach (var segment in segments)
                    {
                        segment.ItemId = baseItem.Id;
                        await CreateSegmentAsync(segment, providerId).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Provider {ProviderName} aborted segment extraction for {MediaPath} due to shutdown", provider.Name, baseItem.Path);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {ProviderName} failed to extract segments from {MediaPath}", provider.Name, baseItem.Path);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mediaSegment.EndTicks, mediaSegment.StartTicks);

        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.MediaSegments.Add(Map(mediaSegment, segmentProviderId));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return mediaSegment;
    }

    /// <inheritdoc />
    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MediaSegments.Where(e => e.Id.Equals(segmentId)).ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        foreach (var provider in _segmentProviders)
        {
            try
            {
                await provider.CleanupExtractedData(itemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider {ProviderName} failed to clean up extracted data for item {ItemId}", provider.Name, itemId);
            }
        }

        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MediaSegments.Where(e => e.ItemId.Equals(itemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem? item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
    {
        if (item is null)
        {
            _logger.LogError("Tried to request segments for an invalid item");
            return [];
        }

        var db = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return BuildSegmentsQuery(db, item, typeFilter, libraryOptions, _segmentProviders, filterByProvider)
                .OrderBy(e => e.StartTicks)
                .AsNoTracking()
                .AsEnumerable()
                .Select(Map)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public IEnumerable<MediaSegmentDto> GetSegments(BaseItem? item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions? libraryOptions, bool filterByProvider = true)
    {
        if (item is null)
        {
            _logger.LogError("Tried to request segments for an invalid item");
            return [];
        }

        using var db = _dbProvider.CreateDbContext();
        return BuildSegmentsQuery(db, item, typeFilter, libraryOptions, _segmentProviders, filterByProvider)
            .OrderBy(e => e.StartTicks)
            .AsNoTracking()
            .AsEnumerable()
            .Select(Map)
            .ToArray();
    }

    /// <summary>
    /// Builds the single shared segment query used by both the async and the synchronous
    /// segment lookups, so the provider-filter logic exists exactly once.
    /// </summary>
    /// <param name="db">The database context to query.</param>
    /// <param name="item">The item whose segments are queried.</param>
    /// <param name="typeFilter">Optional filter restricting the segment types returned.</param>
    /// <param name="libraryOptions">The library options, required when <paramref name="filterByProvider"/> is set.</param>
    /// <param name="providers">The registered media segment providers.</param>
    /// <param name="filterByProvider">When set, restricts segments to currently enabled providers.</param>
    /// <returns>The query. A filter that excludes every provider yields an empty result.</returns>
    private static IQueryable<MediaSegment> BuildSegmentsQuery(JellyfinDbContext db, BaseItem item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions? libraryOptions, IMediaSegmentProvider[] providers, bool filterByProvider)
    {
        var query = db.MediaSegments
            .Where(e => e.ItemId.Equals(item.Id));

        if (typeFilter is not null)
        {
            query = query.Where(e => typeFilter.Contains(e.Type));
        }

        if (filterByProvider)
        {
            // Contains over an empty array translates to a false predicate, so a library
            // with every provider disabled observably returns no segments, as before.
            var providerIds = providers
                .Where(e => !libraryOptions!.DisabledMediaSegmentProviders.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .Select(f => GetProviderId(f.Name))
                .ToArray();
            query = query.Where(e => providerIds.Contains(e.SegmentProviderId));
        }

        return query;
    }

    private static MediaSegmentDto Map(MediaSegment segment)
    {
        return new MediaSegmentDto()
        {
            Id = segment.Id,
            EndTicks = segment.EndTicks,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            Type = segment.Type
        };
    }

    private static MediaSegment Map(MediaSegmentDto segment, string segmentProviderId)
    {
        return new MediaSegment()
        {
            Id = segment.Id,
            EndTicks = segment.EndTicks,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            Type = segment.Type,
            SegmentProviderId = segmentProviderId
        };
    }

    /// <inheritdoc />
    public bool HasSegments(Guid itemId)
    {
        using var db = _dbProvider.CreateDbContext();
        return db.MediaSegments.Any(e => e.ItemId.Equals(itemId));
    }

    /// <inheritdoc/>
    public bool IsTypeSupported(BaseItem baseItem)
    {
        return baseItem.MediaType is Data.Enums.MediaType.Video or Data.Enums.MediaType.Audio;
    }

    /// <inheritdoc/>
    public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item)
    {
        if (item is not (Video or Audio))
        {
            return [];
        }

        return _segmentProviders
            .Select(p => (p.Name, GetProviderId(p.Name)));
    }

    private static string GetProviderId(string name)
        => name.ToLowerInvariant()
            .GetMD5()
            .ToString("N", CultureInfo.InvariantCulture);
}
