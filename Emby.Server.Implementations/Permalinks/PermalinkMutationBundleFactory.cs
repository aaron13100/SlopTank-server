// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-03, 2026-09-07, 2026-09-09.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Discovers and freezes the complete capsule and physical-part closure for a mutation.
/// </summary>
internal sealed class PermalinkMutationBundleFactory
{
    private readonly ILibraryManager _libraryManager;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly string[] _contentRoots;

    public PermalinkMutationBundleFactory(
        ILibraryManager libraryManager,
        PermalinkAuthorityStore authority,
        PermalinkCapsuleStore capsules,
        IPermalinkAtomicFileSystem fileSystem,
        IConfiguration configuration)
    {
        _libraryManager = libraryManager;
        _authority = authority;
        _capsules = capsules;
        _fileSystem = fileSystem;
        _contentRoots = configuration.GetSection("Permalinks:ContentRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    /// <summary>
    /// Builds one deterministic bundle from current durable topology and caller intent.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="request">The mutation request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationBundle> CreateAsync(
        BaseItem item,
        PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        var pathMutation = request.Kind is "path" or "cross-root" or "tree" or "multipart";
        var candidates = DiscoverItems(item, request.Kind);
        var claims = new List<PermalinkMutationClaim>(candidates.Count);
        string? contentRoot = null;
        foreach (var candidate in candidates.OrderBy(value => value.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reservation = await FindReservationAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (reservation is null)
            {
                continue;
            }

            contentRoot ??= reservation.RootPath;
            var capsulePath = await _capsules.PublishGenesisAsync(
                reservation,
                reservation.RootPath,
                candidate.Path,
                cancellationToken).ConfigureAwait(false);
            var snapshot = await _capsules.ReadValidatedAsync(
                capsulePath,
                reservation.CapsuleId,
                reservation.ItemKind,
                cancellationToken).ConfigureAwait(false);
            // A reservation's content root is its immutable genesis. Claims
            // must follow the folded capsule head or the first committed
            // rewrite permanently consumes the only predecessor subsequent
            // rewrites can see.
            var predecessor = snapshot.ContentHead.ContentRoot;
            var claimKind = candidate.Id.Equals(item.Id) ? "content" : "aggregate";
            if (pathMutation)
            {
                predecessor = snapshot.ContentHead.PathAssignment.EventId.ToString("D");
                claimKind = "path";
            }

            claims.Add(new PermalinkMutationClaim(
                reservation.CapsuleId,
                candidate.Id,
                reservation.BindingId,
                predecessor,
                claimKind));
        }

        if (claims.All(claim => !claim.ItemId.Equals(item.Id)) || contentRoot is null)
        {
            throw Conflict(
                "bundle-source-missing",
                $"Protected item '{item.Id}' has no durable capsule predecessor.");
        }

        return new PermalinkMutationBundle(
            claims.OrderBy(claim => claim.CapsuleId).ToArray(),
            CreateParts(item, request.DestinationPath),
            contentRoot,
            request.DestinationPath is null
                ? null
                : ResolveRoot(request.DestinationPath));
    }

    private List<BaseItem> DiscoverItems(BaseItem item, string kind)
    {
        var result = new Dictionary<Guid, BaseItem> { [item.Id] = item };
        if (kind is "path" or "cross-root" or "tree" or "multipart")
        {
            return result.Values.ToList();
        }

        foreach (var parent in item.GetParents().Where(value => value is Season or Series))
        {
            result[parent.Id] = parent;
        }

        if (kind == "aggregate" && item is Folder folder)
        {
            foreach (var descendant in folder.GetRecursiveChildren())
            {
                result[descendant.Id] = descendant;
            }
        }

        var relevantIds = result.Keys.ToHashSet();
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true
        });
        foreach (var boxSet in boxSets.OfType<BoxSet>())
        {
            if (boxSet.LinkedChildren.Any(link =>
                    link.ItemId.HasValue && relevantIds.Contains(link.ItemId.Value)))
            {
                result[boxSet.Id] = boxSet;
            }
        }

        return result.Values.ToList();
    }

    private async Task<PermalinkGenesisReservation?> FindReservationAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        // The anchor lives in the file, so a file that is gone cannot supply
        // one -- but the authority already recorded which reservation held
        // that path, and looking it up there needs no content at all. This is
        // the state of an item the library is deleting BECAUSE its file
        // vanished (the conversion pipeline rewrites .mkv to .mp4), and
        // reading the anchor instead raised ENOENT out of the whole prepare,
        // aborting the folder scan and wedging ingestion for every sibling.
        //
        // Falling back to the recorded path keeps the durable predecessor
        // intact, so the deletion is still journalled against the real capsule
        // rather than being waved through with no claim.
        //
        // Existence is tested rather than catching the read failure so that a
        // file which IS present but unreadable still fails loudly: "gone" and
        // "broken" must not collapse into one silent answer.
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            return await _authority.FindByCurrentPathAsync(item.Path, cancellationToken)
                .ConfigureAwait(false);
        }

        var anchor = _fileSystem.ReadAnchorToken(item.Path);
        return anchor is null
            ? null
            : await _authority.FindByAnchorAsync(anchor, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<PermalinkMutationPart> CreateParts(
        BaseItem item,
        string? destination)
    {
        if (Directory.Exists(item.Path))
        {
            return Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(item.Path, path)
                    .Split(Path.DirectorySeparatorChar)
                    .Contains(".sloptank", StringComparer.Ordinal))
                .OrderBy(path => Path.GetRelativePath(item.Path, path), StringComparer.Ordinal)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(item.Path, path);
                    return new PermalinkMutationPart(
                        "tree:" + relative.Replace(Path.DirectorySeparatorChar, '/'),
                        path,
                        destination is null ? null : Path.Combine(destination, relative));
                })
                .ToArray();
        }

        var parts = new List<PermalinkMutationPart>
        {
            new("main", item.Path, destination)
        };
        if (item is Video video)
        {
            for (var index = 0; index < video.AdditionalParts.Length; index++)
            {
                var source = video.AdditionalParts[index];
                parts.Add(new PermalinkMutationPart(
                    $"additional:{index}",
                    source,
                    destination is null
                        ? null
                        : Path.Combine(
                            Path.GetDirectoryName(destination)!,
                            Path.GetFileName(source))));
            }
        }

        return parts;
    }

    private string ResolveRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var root in _contentRoots)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return root;
            }
        }

        throw new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "content-root-missing",
            $"Destination '{path}' is outside every configured permalink content root.");
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
