// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using BitFaster.Caching.Lru;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Computes the one canonical episode content-completion boundary shared by watched
/// state (server) and the Up Next prompt (client): the start of a terminal outro
/// segment when one exists, otherwise the MaxResumePct fallback (c189).
/// </summary>
public sealed class EpisodeContentEndPolicy
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ILogger<EpisodeContentEndPolicy> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly FastConcurrentLru<string, (EpisodeContentEnd Value, DateTime ExpiresAt)> _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeContentEndPolicy"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="segmentManager">Instance of the <see cref="IMediaSegmentManager"/> interface.</param>
    /// <param name="serviceProvider">Service provider, used only to resolve <see cref="ILibraryManager"/> lazily at call time (constructor-injecting it would be a DI cycle).</param>
    public EpisodeContentEndPolicy(
        ILogger<EpisodeContentEndPolicy> logger,
        IServerConfigurationManager config,
        IMediaSegmentManager segmentManager,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _config = config;
        _segmentManager = segmentManager;
        _serviceProvider = serviceProvider;
        _cache = new FastConcurrentLru<string, (EpisodeContentEnd Value, DateTime ExpiresAt)>(Environment.ProcessorCount, 1024, StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes the canonical content-completion boundary of an episode: the start of a
    /// terminal outro segment when one exists, otherwise the MaxResumePct fallback.
    /// Results are cached per item and runtime for a short TTL.
    /// </summary>
    /// <param name="item">The episode.</param>
    /// <returns>The boundary, or <c>null</c> when the episode has no usable runtime.</returns>
    public EpisodeContentEnd? GetEpisodeContentEnd(Episode item)
    {
        var runtimeTicks = item.GetRunTimeTicksForPlayState();
        if (runtimeTicks <= 0)
        {
            return null;
        }

        var key = $"{item.Id:N}:{runtimeTicks}";
        if (_cache.TryGet(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        {
            return entry.Value;
        }

        var result = Compute(item, runtimeTicks);
        _cache.AddOrUpdate(key, (result, DateTime.UtcNow.Add(CacheTtl)));
        return result;
    }

    /// <summary>
    /// Computes the boundary without the cache.
    /// </summary>
    /// <param name="item">The episode.</param>
    /// <param name="runtimeTicks">The episode's play-state runtime in ticks.</param>
    /// <returns>The computed boundary.</returns>
    private EpisodeContentEnd Compute(Episode item, long runtimeTicks)
    {
        LibraryOptions? libraryOptions = null;
        var filterByProvider = false;
        if (_serviceProvider.GetService(typeof(ILibraryManager)) is ILibraryManager libraryManager)
        {
            libraryOptions = libraryManager.GetLibraryOptions(item);
            filterByProvider = true;
        }
        else
        {
            _logger.LogDebug("ILibraryManager unavailable; computing content end for {ItemName} without provider filtering", item.Name);
        }

        long? terminalOutroStart = null;
        foreach (var segment in _segmentManager.GetSegments(item, new[] { MediaSegmentType.Outro }, libraryOptions, filterByProvider))
        {
            if (segment.StartTicks > 0
                && segment.EndTicks > segment.StartTicks
                && segment.EndTicks >= runtimeTicks
                && (terminalOutroStart is null || segment.StartTicks > terminalOutroStart))
            {
                terminalOutroStart = segment.StartTicks;
            }
        }

        if (terminalOutroStart is not null)
        {
            return new EpisodeContentEnd(terminalOutroStart.Value, EpisodeContentEndSource.TerminalOutro);
        }

        var fallbackTicks = (long)(runtimeTicks * (decimal)_config.Configuration.MaxResumePct / 100);
        return new EpisodeContentEnd(fallbackTicks, EpisodeContentEndSource.RuntimePercentage);
    }
}
