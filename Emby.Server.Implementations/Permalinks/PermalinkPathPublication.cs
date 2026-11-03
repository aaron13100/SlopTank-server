// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Publishes the immutable content event that records a guarded path relocation.
/// </summary>
internal sealed class PermalinkPathPublication
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    /// <summary>Initializes a path publication coordinator.</summary>
    /// <param name="authority">The reservation authority.</param>
    /// <param name="capsules">The immutable capsule store.</param>
    /// <param name="transitions">The content-transition authority.</param>
    /// <param name="documents">The canonical event factory.</param>
    /// <param name="bindings">The derived binding index.</param>
    /// <param name="fileSystem">The stable-anchor filesystem.</param>
    public PermalinkPathPublication(
        PermalinkAuthorityStore authority,
        PermalinkCapsuleStore capsules,
        PermalinkTransitionStore transitions,
        PermalinkDocumentFactory documents,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _authority = authority;
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Publishes or resumes the exact path successor owned by a prepared operation.
    /// </summary>
    /// <param name="operation">The immutable path operation.</param>
    /// <param name="item">The moved library item.</param>
    /// <param name="bundle">The exact claimed path bundle.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the asynchronous publication.</returns>
    public async Task PublishAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        var currentPath = item.Path;
        var targetRoot = bundle.DestinationRootPath ?? bundle.ContentRootPath;
        var mainParts = bundle.Parts.Where(part => part.Role == "main").ToArray();
        var tree = bundle.Parts.All(part => part.Role.StartsWith("tree:", StringComparison.Ordinal));
        if (!item.Id.Equals(operation.ItemId) || mainParts.Length > 1 || (mainParts.Length == 0 && !tree))
        {
            throw Conflict("path-item-mismatch", "The operation must identify the moved item and its exact file or tree bundle.");
        }

        var plannedPath = mainParts.FirstOrDefault()?.DestinationPath ?? operation.DestinationPath;
        if (plannedPath is null || !string.Equals(plannedPath, currentPath, StringComparison.Ordinal))
        {
            throw Conflict("path-destination-mismatch", "The moved item path does not match its immutable destination.");
        }

        var anchorToken = _fileSystem.ReadAnchorToken(currentPath)
            ?? throw Conflict("path-anchor-missing", "The moved item has no stable permalink anchor.");
        var reservation = await _authority.FindByAnchorAsync(anchorToken, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("path-reservation-missing", "The moved item's stable anchor has no authority reservation.");
        if (!reservation.CapsuleId.Equals(operation.CapsuleId))
        {
            throw Conflict("path-reservation-mismatch", "The live anchor does not match the operation's exact path binding.");
        }

        var pathClaims = bundle.Claims.Where(claim =>
            claim.CapsuleId.Equals(operation.CapsuleId) && claim.Kind == "path").ToArray();
        if (pathClaims.Length != 1
            || !pathClaims[0].ItemId.Equals(operation.ItemId)
            || !pathClaims[0].BindingInstanceId.Equals(reservation.BindingId)
            || !Guid.TryParseExact(pathClaims[0].Predecessor, "D", out var previousPathEventId))
        {
            throw Conflict("path-claim-mismatch", "The bundle must contain one exact path claim for the reserved capsule and binding.");
        }

        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            targetRoot,
            currentPath,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        if (!snapshot.ContentHead.BindingInstanceId.Equals(reservation.BindingId))
        {
            throw Conflict("path-binding-mismatch", "The capsule content head does not match the live anchor binding.");
        }

        var elections = await _transitions.FindContentTransitionsByOperationAsync(
            reservation.CapsuleId,
            operation.OperationId,
            cancellationToken).ConfigureAwait(false);
        PermalinkEventDocument successor;
        if (elections.Count == 0)
        {
            if (!snapshot.ContentHead.PathAssignment.EventId.Equals(previousPathEventId)
                || !string.Equals(snapshot.ContentHead.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal))
            {
                throw Conflict("path-predecessor-mismatch", "The live capsule no longer matches the operation's frozen path predecessor and content root.");
            }

            var competing = await _transitions.FindContentTransitionAsync(
                reservation.CapsuleId,
                snapshot.ContentHead.EventId,
                cancellationToken).ConfigureAwait(false);
            if (competing is not null)
            {
                throw Conflict("path-election-conflict", "The content predecessor already has a different elected successor.");
            }

            successor = _documents.CreateControlledPathSuccessor(
                snapshot,
                operation,
                bundle,
                currentPath);
            var bytes = CanonicalJson.Serialize(successor);
            await _capsules.StageContentAsync(capsulePath, successor, bytes, cancellationToken).ConfigureAwait(false);
            await _transitions.AuthorizeContentTransitionAsync(
                reservation.CapsuleId,
                snapshot.ContentHead.EventId,
                successor,
                bytes,
                cancellationToken).ConfigureAwait(false);
            await _capsules.AppendContentAsync(
                capsulePath,
                successor,
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (elections.Count != 1)
            {
                throw Conflict("path-election-ambiguous", "The operation owns multiple content successor elections.");
            }

            var election = elections[0];
            successor = await _capsules.ReadPublicationAsync(
                capsulePath,
                election.EventId,
                cancellationToken).ConfigureAwait(false);
            var predecessor = snapshot.Events.SingleOrDefault(value =>
                value.EventId.ToString("D") == election.Predecessor);
            if (!string.Equals(election.Predecessor, successor.PreviousContentEventId?.ToString("D"), StringComparison.Ordinal)
                || !successor.OperationId.Equals(operation.OperationId)
                || successor.Kind != "controlled-mutation"
                || !successor.PathAssignment.PreviousPathEventId.Equals(previousPathEventId)
                || !successor.PathAssignment.EventId.Equals(successor.EventId)
                || !string.Equals(successor.PathAssignment.Path, currentPath, StringComparison.Ordinal)
                || !string.Equals(successor.PathAssignment.RootPath, targetRoot, StringComparison.Ordinal)
                || !string.Equals(successor.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal)
                || !successor.BindingInstanceId.Equals(reservation.BindingId)
                || predecessor is null
                || !SameContentIdentity(predecessor, successor)
                || !await _transitions.IsAuthorizedContentEventAsync(
                    reservation.CapsuleId,
                    successor,
                    cancellationToken).ConfigureAwait(false))
            {
                throw Conflict("path-election-mismatch", "The elected event does not match the operation, frozen path, and unchanged title content.");
            }

            await _capsules.AppendContentAsync(
                capsulePath,
                successor,
                CanonicalJson.Serialize(successor),
                cancellationToken).ConfigureAwait(false);
        }

        snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        if (!snapshot.ContentHead.PathAssignment.EventId.Equals(successor.EventId)
            || !string.Equals(snapshot.ContentHead.PathAssignment.Path, currentPath, StringComparison.Ordinal)
            || !string.Equals(snapshot.ContentHead.PathAssignment.RootPath, targetRoot, StringComparison.Ordinal))
        {
            throw Conflict("path-location-advanced", "A newer path assignment exists; refusing to replace its derived current location.");
        }

        if (!await _transitions.IsAuthorizedContentEventAsync(
            reservation.CapsuleId,
            successor,
            cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("path-event-unauthorized", "The published path event is not authorized by the transition authority.");
        }

        await _bindings.UpdateCurrentLocationAsync(
            reservation,
            reservation with
            {
                RootPath = targetRoot,
                CapsulePath = capsulePath,
                CurrentPath = currentPath
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the full-history capsule at the current location of a moved tree member.
    /// </summary>
    /// <param name="member">The already moved library member.</param>
    /// <param name="bundle">The exact claimed tree bundle.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the asynchronous publication.</returns>
    public async Task PublishMemberLocationAsync(
        BaseItem member,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        var currentPath = member.Path;
        var anchorToken = _fileSystem.ReadAnchorToken(currentPath);
        if (anchorToken is null)
        {
            return;
        }

        var reservation = await _authority.FindByAnchorAsync(anchorToken, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict(
                "path-reservation-missing",
                $"Moved tree member '{member.Id}' has no authority reservation for its stable anchor.");
        var targetRoot = bundle.DestinationRootPath ?? bundle.ContentRootPath;
        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            targetRoot,
            currentPath,
            cancellationToken).ConfigureAwait(false);
        _ = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        await _bindings.UpdateCurrentLocationAsync(
            reservation,
            reservation with
            {
                RootPath = targetRoot,
                CapsulePath = capsulePath,
                CurrentPath = currentPath
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool SameContentIdentity(
        PermalinkEventDocument predecessor,
        PermalinkEventDocument successor)
    {
        return predecessor.ContentRoot == successor.ContentRoot
            && predecessor.BindingInstanceId.Equals(successor.BindingInstanceId)
            && predecessor.Leaves.SequenceEqual(successor.Leaves)
            && predecessor.AcceptedProviderClaim.SequenceEqual(successor.AcceptedProviderClaim)
            && predecessor.ActiveExternalAliases.SequenceEqual(successor.ActiveExternalAliases);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
