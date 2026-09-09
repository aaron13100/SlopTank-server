// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Claims, publishes, and verifies immutable capsule successors.
/// </summary>
internal sealed class PermalinkTransitionPublisher
{
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkTransitionPublisher"/> class.
    /// </summary>
    /// <param name="transitions">The transitions.</param>
    /// <param name="capsules">The capsules.</param>
    /// <param name="documents">The documents.</param>
    public PermalinkTransitionPublisher(
        PermalinkTransitionStore transitions,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents)
    {
        _transitions = transitions;
        _capsules = capsules;
        _documents = documents;
    }

    /// <summary>
    /// Verifies exact content or publishes one authorized strict-superset successor.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="capsulePath">The durable capsule path.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkCapsuleSnapshot> ReconcileContentAsync(
        PermalinkStoreRequest request,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                request.ContentRoot,
                snapshot.ContentHead.ContentRoot,
                StringComparison.Ordinal))
        {
            return snapshot;
        }

        if (request.ItemKind is not "Series" and not "Season"
            || !IsStrictSuperset(snapshot.ContentHead.Leaves, request.Leaves))
        {
            throw Conflict(
                "content-mismatch",
                $"Current content for '{request.Path}' does not match capsule '{snapshot.Header.CapsuleId}'.");
        }

        var successor = _documents.CreateContentSuccessor(request, snapshot);
        var bytes = CanonicalJson.Serialize(successor);
        await _transitions.AuthorizeContentTransitionAsync(
            snapshot.Header.CapsuleId,
            snapshot.ContentHead.EventId,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendContentAsync(
            capsulePath,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        return await _capsules.ReadValidatedAsync(
            capsulePath,
            snapshot.Header.CapsuleId,
            snapshot.Header.ItemKind,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns existing aliases or elects, publishes, and verifies the first alias.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="reservation">The reservation.</param>
    /// <param name="capsulePath">The durable capsule path.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<IReadOnlyList<string>> GetOrPublishAliasesAsync(
        PermalinkStoreRequest request,
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var aliases = await _transitions.GetAliasesAsync(
            reservation.CapsuleId,
            cancellationToken).ConfigureAwait(false);
        if (aliases.Count == 0 && request.PublishAlias)
        {
            aliases = request.PreferredAlias is null
                ? await PublishFallbackAliasAsync(
                    reservation,
                    capsulePath,
                    snapshot,
                    cancellationToken).ConfigureAwait(false)
                : await PublishExternalAliasAsync(
                    reservation,
                    capsulePath,
                    snapshot,
                    request.PreferredAlias,
                    cancellationToken).ConfigureAwait(false);
        }

        if (reservation.IssuedId is not null && reservation.IssuanceNonce.HasValue)
        {
            await _transitions.ValidateIssuanceAsync(
                reservation.IssuedId,
                reservation.IssuanceNonce.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return aliases;
    }

    private async Task<IReadOnlyList<string>> PublishFallbackAliasAsync(
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var issuance = await _transitions.AllocateAsync(cancellationToken).ConfigureAwait(false);
        var aliasEvent = _documents.CreateAliasSuccessor(
            reservation,
            snapshot,
            issuance);
        var bytes = CanonicalJson.Serialize(aliasEvent);
        var claim = await _transitions.ClaimFirstAliasAsync(
            reservation.CapsuleId,
            issuance,
            aliasEvent,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendAliasAsync(
            capsulePath,
            claim,
            cancellationToken).ConfigureAwait(false);
        await _transitions.ValidateIssuanceAsync(
            claim.PermalinkId,
            claim.IssuanceNonce
                ?? throw Conflict(
                    "issuance-missing",
                    $"Fallback alias '{claim.PermalinkId}' has no issuance nonce."),
            cancellationToken).ConfigureAwait(false);
        return [claim.PermalinkId];
    }

    private async Task<IReadOnlyList<string>> PublishExternalAliasAsync(
        PermalinkGenesisReservation reservation,
        string capsulePath,
        PermalinkCapsuleSnapshot snapshot,
        string alias,
        CancellationToken cancellationToken)
    {
        var aliasEvent = _documents.CreateExternalAliasSuccessor(
            reservation,
            snapshot,
            alias);
        var bytes = CanonicalJson.Serialize(aliasEvent);
        var claim = await _transitions.ClaimExternalAliasAsync(
            reservation.CapsuleId,
            alias,
            aliasEvent,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendAliasAsync(
            capsulePath,
            claim,
            cancellationToken).ConfigureAwait(false);
        if (claim.IssuanceNonce.HasValue)
        {
            await _transitions.ValidateIssuanceAsync(
                claim.PermalinkId,
                claim.IssuanceNonce.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return [claim.PermalinkId];
    }

    private static bool IsStrictSuperset(
        IReadOnlyList<PermalinkLeaf> previous,
        IReadOnlyList<PermalinkLeaf> current)
    {
        return current.Count > previous.Count
            && previous.All(oldLeaf => current.Contains(oldLeaf));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
