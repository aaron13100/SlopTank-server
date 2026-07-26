using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Coordinates one recoverable authority election with portable capsule publication.
/// </summary>
internal sealed class PermalinkStore : IPermalinkStore
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkTransitionPublisher _publisher;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly string[] _contentRoots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkStore"/> class.
    /// </summary>
    public PermalinkStore(
        PermalinkAuthorityStore authority,
        PermalinkTransitionStore transitions,
        PermalinkBindingIndex bindings,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkTransitionPublisher publisher,
        IPermalinkAtomicFileSystem fileSystem,
        IConfiguration configuration)
    {
        _authority = authority;
        _transitions = transitions;
        _bindings = bindings;
        _capsules = capsules;
        _documents = documents;
        _publisher = publisher;
        _fileSystem = fileSystem;
        _contentRoots = configuration.GetSection("Permalinks:ContentRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        EnsureReachableOrVirtualSeason(request);
        var root = ResolveRoot(request.Path);
        var anchorToken = await _fileSystem.GetOrCreateAnchorTokenAsync(
            request.Path,
            cancellationToken).ConfigureAwait(false);

        var samePath = await _authority.FindByCurrentPathAsync(
            request.Path,
            cancellationToken).ConfigureAwait(false);
        if (samePath is not null
            && !string.Equals(samePath.AnchorToken, anchorToken, StringComparison.Ordinal))
        {
            throw Conflict(
                "anchor-replacement",
                $"Path '{request.Path}' replaced its stable anchor and cannot remint.");
        }

        var reservation = await _authority.FindByAnchorAsync(
            anchorToken,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            reservation = await ReserveGenesisAsync(
                request,
                root,
                anchorToken,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateAdoptionCandidate(reservation, request);
        }

        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            root,
            request.Path,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        snapshot = await _publisher.ReconcileContentAsync(
            request,
            capsulePath,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        var aliases = await _publisher.GetOrPublishAliasesAsync(
            request,
            reservation,
            capsulePath,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        foreach (var alias in aliases)
        {
            await _bindings.BindAsync(
                alias,
                request.ItemId,
                snapshot.ContentHead.ContentRoot,
                cancellationToken).ConfigureAwait(false);
        }

        await _bindings.UpdateCurrentPathAsync(
            anchorToken,
            request.Path,
            cancellationToken).ConfigureAwait(false);
        return new PermalinkStoredState(
            reservation.CapsuleId,
            snapshot.ContentHead.ContentRoot,
            aliases);
    }

    private async Task<PermalinkGenesisReservation> ReserveGenesisAsync(
        PermalinkStoreRequest request,
        string root,
        string anchorToken,
        CancellationToken cancellationToken)
    {
        var issuance = request.PublishAlias && request.PreferredAlias is null
            ? await _transitions.AllocateAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var capsuleId = Guid.NewGuid();
        var capsulePath = _capsules.GetCapsulePath(
            root,
            request.Path,
            request.ItemKind,
            capsuleId);
        var candidate = _documents.CreateGenesis(
            request,
            root,
            anchorToken,
            capsulePath,
            capsuleId,
            issuance);
        return await _authority.ReserveGenesisAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureReachableOrVirtualSeason(PermalinkStoreRequest request)
    {
        if (File.Exists(request.Path) || Directory.Exists(request.Path))
        {
            return;
        }

        if (request.ItemKind == "Season"
            && request.Path.Contains(
                $"{Path.DirectorySeparatorChar}permalink-virtual-seasons{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            _fileSystem.CreateDirectoryDurable(request.Path);
            return;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "content-unreachable",
            $"Permalink content is unreachable at '{request.Path}'.");
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
            $"Path '{path}' is outside every configured permalink content root.");
    }

    private static void ValidateAdoptionCandidate(
        PermalinkGenesisReservation reservation,
        PermalinkStoreRequest request)
    {
        if (!string.Equals(reservation.ItemKind, request.ItemKind, StringComparison.Ordinal))
        {
            throw Conflict(
                "kind-mismatch",
                $"Anchor belongs to {reservation.ItemKind}, not {request.ItemKind}.");
        }

        if (!string.Equals(reservation.CurrentPath, request.Path, StringComparison.Ordinal)
            && (File.Exists(reservation.CurrentPath) || Directory.Exists(reservation.CurrentPath)))
        {
            throw Conflict(
                "copied-anchor",
                $"Stable anchor is live at both '{reservation.CurrentPath}' and '{request.Path}'; copied binding must be forked.");
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
