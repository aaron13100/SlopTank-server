using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Orchestrates the single permalink issuance path outside metadata processing.
/// </summary>
public sealed class PermalinkManager : IPermalinkManager
{
    private readonly IPermalinkStore _store;
    private readonly PermalinkEvidence _evidence;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkManager"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="evidence">The evidence.</param>
    public PermalinkManager(IPermalinkStore store, PermalinkEvidence evidence)
    {
        _store = store;
        _evidence = evidence;
    }

    /// <inheritdoc />
    public async Task<PermalinkResponse> EnsurePermalinkIdsAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var external = GetCanonicalExternalId(item);
        var state = await EnsureCoreAsync(
            item,
            publishAlias: true,
            external,
            new HashSet<Guid>(),
            cancellationToken).ConfigureAwait(false);
        if (state.Aliases.Count == 0)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "seed-not-published",
                $"Capsule '{state.CapsuleId}' remains seeded-unpublished.");
        }

        return new PermalinkResponse(1, state.Aliases, state.Aliases[0]);
    }

    private async Task<PermalinkStoredState> EnsureCoreAsync(
        BaseItem item,
        bool publishAlias,
        string? preferredAlias,
        HashSet<Guid> ancestry,
        CancellationToken cancellationToken)
    {
        ValidateEligibility(item);
        if (!ancestry.Add(item.Id))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "aggregate-cycle-pending",
                $"BoxSet cycle at '{item.Name}' requires reserved SCC initialization.");
        }

        var itemLock = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveLocalPath(item);
            var evidence = item switch
            {
                Series series => await ComputeSeriesAsync(
                    series,
                    ancestry,
                    cancellationToken).ConfigureAwait(false),
                Season season => await ComputeSeasonAsync(
                    season,
                    ancestry,
                    cancellationToken).ConfigureAwait(false),
                BoxSet boxSet => await ComputeBoxSetAsync(
                    boxSet,
                    ancestry,
                    cancellationToken).ConfigureAwait(false),
                _ => await _evidence.ComputeContentItemAsync(item, cancellationToken)
                    .ConfigureAwait(false)
            };
            if (evidence.Leaves.Count == 0)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "empty-manifest",
                    $"{item.GetType().Name} '{item.Name}' has no identity leaves.");
            }

            return await _store.EnsureAsync(
                new PermalinkStoreRequest(
                    item.Id,
                    item.GetType().Name,
                    path,
                    evidence.ContentRoot,
                    evidence.Leaves,
                    publishAlias,
                    preferredAlias),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = ancestry.Remove(item.Id);
            itemLock.Release();
        }
    }

    private async Task<PermalinkEvidenceResult> ComputeSeriesAsync(
        Series series,
        HashSet<Guid> ancestry,
        CancellationToken cancellationToken)
    {
        var descendants = new List<(BaseItem, PermalinkStoredState)>();
        foreach (var season in series.GetRecursiveChildren().OfType<Season>())
        {
            _ = await EnsureCoreAsync(
                season,
                publishAlias: false,
                preferredAlias: null,
                ancestry,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var child in series.GetRecursiveChildren().Where(IsContentItem))
        {
            var state = await EnsureCoreAsync(
                child,
                publishAlias: false,
                preferredAlias: null,
                ancestry,
                cancellationToken).ConfigureAwait(false);
            descendants.Add((child, state));
        }

        return _evidence.ComputeDescendantManifest(descendants);
    }

    private async Task<PermalinkEvidenceResult> ComputeSeasonAsync(
        Season season,
        HashSet<Guid> ancestry,
        CancellationToken cancellationToken)
    {
        var descendants = new List<(BaseItem, PermalinkStoredState)>();
        foreach (var child in season.GetRecursiveChildren().Where(IsContentItem))
        {
            var state = await EnsureCoreAsync(
                child,
                publishAlias: false,
                preferredAlias: null,
                ancestry,
                cancellationToken).ConfigureAwait(false);
            descendants.Add((child, state));
        }

        return _evidence.ComputeDescendantManifest(descendants);
    }

    private async Task<PermalinkEvidenceResult> ComputeBoxSetAsync(
        BoxSet boxSet,
        HashSet<Guid> ancestry,
        CancellationToken cancellationToken)
    {
        var members = new List<(BaseItem, PermalinkStoredState)>();
        foreach (var member in boxSet.GetLinkedChildren())
        {
            var state = await EnsureCoreAsync(
                member,
                publishAlias: false,
                preferredAlias: null,
                ancestry,
                cancellationToken).ConfigureAwait(false);
            members.Add((member, state));
        }

        return _evidence.ComputeBoxSetManifest(boxSet.Id, members);
    }

    private static void ValidateEligibility(BaseItem item)
    {
        if (item is not Movie and not Episode and not Video and not MusicVideo
            and not Series and not Season and not BoxSet)
        {
            throw Ineligible(item, "kind is unsupported");
        }

        if (item.ExtraType.HasValue)
        {
            throw Ineligible(item, "extras and trailers are ineligible");
        }

        if (item is not Season { Path: null } && !item.IsFileProtocol)
        {
            throw Ineligible(item, "item is remote");
        }
    }

    private static string ResolveLocalPath(BaseItem item)
    {
        if (!string.IsNullOrEmpty(item.Path))
        {
            return item.Path;
        }

        if (item is Season season)
        {
            var series = season.GetParents().OfType<Series>().FirstOrDefault();
            if (series is not null && !string.IsNullOrEmpty(series.Path))
            {
                var slot = season.IndexNumber?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                return Path.Combine(
                    series.Path,
                    ".sloptank",
                    "permalink-virtual-seasons",
                    slot);
            }
        }

        throw Ineligible(item, "no local physical ancestor");
    }

    private static bool IsContentItem(BaseItem item)
    {
        return item is Movie or Episode or Video or MusicVideo;
    }

    private static string? GetCanonicalExternalId(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue("Imdb", out var imdb)
            && imdb.StartsWith("tt", StringComparison.Ordinal)
            && imdb.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return imdb;
        }

        if (!item.ProviderIds.TryGetValue("Tmdb", out var tmdb)
            || tmdb.Length == 0
            || tmdb[0] == '0'
            || tmdb.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return null;
        }

        var qualifier = item switch
        {
            Movie => "mv",
            Series => "tv",
            Episode => "ep",
            Season => "se",
            BoxSet => "co",
            _ => null
        };
        return qualifier is null ? null : $"tm-{qualifier}-{tmdb}";
    }

    private static PermalinkException Ineligible(BaseItem item, string reason)
    {
        return new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "item-ineligible",
            $"Permalink item '{item.Id}' is ineligible: {reason}.");
    }
}
