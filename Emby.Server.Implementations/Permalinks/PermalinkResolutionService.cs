using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Revalidates physical and logical identity before metadata or playback leaves the server.
/// </summary>
internal sealed class PermalinkResolutionService : IPermalinkResolutionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPermalinkManager _manager;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkLeaseStore _leases;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;

    public PermalinkResolutionService(
        ILibraryManager libraryManager,
        IPermalinkManager manager,
        PermalinkBindingIndex bindings,
        PermalinkLeaseStore leases,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal)
    {
        _libraryManager = libraryManager;
        _manager = manager;
        _bindings = bindings;
        _leases = leases;
        _fileSystem = fileSystem;
        _authority = authority;
        _journal = journal;
    }

    public async Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (purpose is not "details" and not "playback")
        {
            throw Conflict("purpose-invalid", $"Unknown permalink purpose '{purpose}'.");
        }

        var candidates = await _bindings.FindResolutionBindingsAsync(
            permalinkId,
            cancellationToken).ConfigureAwait(false);
        var result = new List<PermalinkCandidateEnvelope>(candidates.Count);
        foreach (var candidate in candidates)
        {
            await VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
            result.Add(await _leases.IssueAsync(
                candidate,
                purpose,
                userId,
                cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<BaseItem> RedeemDetailsAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var document = _leases.Consume(handle, lease, "details", userId);
        var binding = await _bindings.FindResolutionBindingAsync(
            document.PermalinkId,
            document.ItemId,
            cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(binding, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var document = _leases.Consume(handle, lease, "playback", userId);
        var binding = await _bindings.FindResolutionBindingAsync(
            document.PermalinkId,
            document.ItemId,
            cancellationToken).ConfigureAwait(false);
        var item = await VerifyAsync(binding, cancellationToken).ConfigureAwait(false);
        var queueCount = item is Series or Season
            ? Math.Min(100, ((Folder)item).GetRecursiveChildren().Count(IsPlayable))
            : 1;
        if (item is Series or Season)
        {
            return new PermalinkPlaybackSnapshot(item.Id, string.Empty, queueCount);
        }

        var root = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-playback-leases",
            handle,
            "entries",
            "0",
            "snapshots");
        _fileSystem.CreateDirectoryDurable(root);
        var snapshot = Path.Combine(root, Path.GetFileName(item.Path));
        File.Copy(item.Path, snapshot);
        return new PermalinkPlaybackSnapshot(item.Id, snapshot, queueCount);
    }

    private async Task<BaseItem> VerifyAsync(
        PermalinkResolutionBinding binding,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById<BaseItem>(binding.ItemId)
            ?? throw Conflict("binding-item-missing", $"Bound item '{binding.ItemId}' is missing.");
        if (await _journal.HasPendingAsync(item.Id, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "identity-mutation-pending",
                $"Item '{item.Id}' has an unfinished durable identity mutation.");
        }

        var aliases = await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!binding.IsCapsuleOverride
            && !aliases.Ids.Contains(binding.PermalinkId, StringComparer.Ordinal))
        {
            throw Conflict("assignment-mismatch", "The requested alias is not active for this assignment.");
        }

        var anchor = _fileSystem.ReadAnchorToken(item.Path);
        if (!string.Equals(anchor, binding.AnchorToken, StringComparison.Ordinal)
            || !string.Equals(item.Path, binding.CurrentPath, StringComparison.Ordinal))
        {
            throw Conflict("binding-replaced", "The physical binding or stable anchor changed.");
        }

        if (!binding.PermalinkId.StartsWith("sk-", StringComparison.Ordinal)
            && !CurrentExternalAliases(item).Contains(binding.PermalinkId, StringComparer.Ordinal))
        {
            throw Conflict("assignment-mismatch", "Current provider assignment does not accept this alias.");
        }

        return item;
    }

    private static IEnumerable<string> CurrentExternalAliases(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue("Imdb", out var imdb))
        {
            yield return imdb;
        }

        if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb))
        {
            var qualifier = item is MediaBrowser.Controller.Entities.Movies.Movie ? "mv"
                : item is Series ? "tv"
                : item is MediaBrowser.Controller.Entities.TV.Episode ? "ep"
                : item is Season ? "se"
                : "co";
            yield return $"tm-{qualifier}-{tmdb}";
        }
    }

    private static bool IsPlayable(BaseItem item)
    {
        return item is Video;
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
