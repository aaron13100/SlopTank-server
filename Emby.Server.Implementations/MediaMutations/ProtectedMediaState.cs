// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Permalinks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.MediaMutations;

/// <summary>
/// Maintains the existing guarded file-replacement integrity state. Reuses the
/// committed replacement implementation; logical content IDs remain in the
/// application's metadata mapping and do not depend on this file-write state.
/// </summary>
internal sealed class ProtectedMediaState
{
    private readonly IPermalinkStore _store;
    private readonly PermalinkEvidence _evidence;
    private readonly ILibraryManager _libraryManager;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtectedMediaState"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="libraryManager">The library manager, used to enumerate local alternate versions.</param>
    public ProtectedMediaState(
        IPermalinkStore store,
        PermalinkEvidence evidence,
        ILibraryManager libraryManager)
    {
        _store = store;
        _evidence = evidence;
        _libraryManager = libraryManager;
    }

    /// <summary>Establishes replacement safety when a guarded file mutation is prepared.</summary>
    /// <param name="item">The persisted library item whose files will change.</param>
    /// <param name="cancellationToken">The mutation's cancellation token.</param>
    /// <returns>Private replacement state used by the existing mutation journal.</returns>
    public Task<PermalinkStoredState> EnsureAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return EnsureCoreAsync(
            item,
            publishAlias: true,
            preferredAlias: null,
            new HashSet<Guid>(),
            cancellationToken);
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
            // Request cancellation is an admission signal, not the lifetime of an admitted
            // durable mutation. Once this item lock is held, interruption can leave authority,
            // capsule, alias, and binding publication at different durable steps while the next
            // resolver waits behind this lock. Finish or fail the admitted transaction on its own
            // lifetime so a browser navigation cannot strand a half-minted alias.
            var mutationCancellationToken = CancellationToken.None;
            var path = ResolveLocalPath(item);
            IPermalinkPathLease? lease = null;
            try
            {
                var physicalKind = item.GetType().Name;
                if (IsContentItem(item))
                {
                    // Anchor creation changes macOS ctime. Establish it before evidence so the
                    // digest is cached against the stable post-anchor token on the genesis call.
                    // The lease is held from here through Ensure, so the path key (item lock
                    // first, then path key) cannot change underneath the evidence gap.
                    // Aggregates take theirs just before Ensure: they recurse into children
                    // that take their own, and a virtual Season directory may not exist until
                    // the store validates and creates it.
                    lease = await _store.BeginContentIdentityAsync(
                        path,
                        physicalKind,
                        mutationCancellationToken).ConfigureAwait(false);
                    physicalKind = await _store.GetReservedPhysicalKindAsync(
                        lease,
                        mutationCancellationToken).ConfigureAwait(false) ?? physicalKind;
                }

                var evidence = item switch
                {
                    Series series => await ComputeSeriesAsync(
                        series,
                        ancestry,
                        mutationCancellationToken).ConfigureAwait(false),
                    Season season => await ComputeSeasonAsync(
                        season,
                        ancestry,
                        mutationCancellationToken).ConfigureAwait(false),
                    BoxSet boxSet => await ComputeBoxSetAsync(
                        boxSet,
                        ancestry,
                        mutationCancellationToken).ConfigureAwait(false),
                    _ => await _evidence.ComputeContentItemAsync(
                        item,
                        physicalKind,
                        mutationCancellationToken)
                        .ConfigureAwait(false)
                };
                if (evidence.Leaves.Count == 0)
                {
                    throw new PermalinkException(
                        PermalinkErrorKind.Conflict,
                        "empty-manifest",
                        $"{item.GetType().Name} '{item.Name}' has no identity leaves.");
                }

                lease ??= await _store.BeginContentIdentityAsync(
                    path,
                    physicalKind,
                    mutationCancellationToken).ConfigureAwait(false);
                return await _store.EnsureAsync(
                    new PermalinkStoreRequest(
                        item.Id,
                        physicalKind,
                        path,
                        evidence.ContentRoot,
                        evidence.Leaves,
                        publishAlias,
                        preferredAlias),
                    lease,
                    mutationCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
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

        foreach (var child in EnumerateContentDescendants(series))
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
        foreach (var child in EnumerateContentDescendants(season))
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

    /// <summary>
    /// Enumerates the content items an aggregate's descendant manifest covers:
    /// every content descendant plus every local alternate version of each.
    /// The library query behind <c>GetRecursiveChildren</c> hides alternates
    /// (they carry a primary version id), so a file that gains a better sibling
    /// would otherwise drop out of the manifest and read as member replacement
    /// instead of growth. Linked alternate versions are user-linked across
    /// folders and are not members of the aggregate, so they stay excluded.
    /// </summary>
    /// <param name="aggregate">The Series or Season.</param>
    /// <returns>Content items deduplicated by id, primaries before their alternates.</returns>
    private List<BaseItem> EnumerateContentDescendants(Folder aggregate)
    {
        var result = new Dictionary<Guid, BaseItem>();
        foreach (var child in aggregate.GetRecursiveChildren().Where(IsContentItem))
        {
            result[child.Id] = child;
            if (child is not Video video)
            {
                continue;
            }

            foreach (var alternateId in _libraryManager.GetLocalAlternateVersionIds(video))
            {
                if (!result.ContainsKey(alternateId)
                    && _libraryManager.GetItemById(alternateId) is Video alternate)
                {
                    result[alternate.Id] = alternate;
                }
            }
        }

        return result.Values.ToList();
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

    private static PermalinkException Ineligible(BaseItem item, string reason)
    {
        return new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "item-ineligible",
            $"Protected media item '{item.Id}' is ineligible: {reason}.");
    }
}
