using System;
using System.Globalization;
using System.Security.Cryptography;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Constructs immutable canonical genesis and successor records.
/// </summary>
internal sealed class PermalinkDocumentFactory
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkDocumentFactory"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    public PermalinkDocumentFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Freezes one candidate for the global stable-anchor genesis election.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="root">The root.</param>
    /// <param name="anchorToken">The anchor token.</param>
    /// <param name="capsulePath">The durable capsule path.</param>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="issuance">The issuance.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkGenesisCandidate CreateGenesis(
        PermalinkStoreRequest request,
        string root,
        string anchorToken,
        string capsulePath,
        Guid capsuleId,
        PermalinkIssuance? issuance)
    {
        var createdAt = UtcNow();
        var bindingId = NewGuid();
        var operationId = NewGuid();
        var pathEventId = NewGuid();
        var header = new PermalinkCapsuleDocument(
            capsuleId,
            request.ItemKind,
            createdAt);
        var ids = issuance is null
            ? []
            : new[]
            {
                new PermalinkEventAlias(
                    issuance.Id,
                    issuance.IssuanceNonce,
                    issuance.CreatedAt)
            };
        var providerClaims = request.PreferredAlias is null
            ? []
            : new[] { ToProviderClaim(request.PreferredAlias) };
        var activeExternalAliases = request.PublishAlias && request.PreferredAlias is not null
            ? new[] { request.PreferredAlias }
            : [];
        var eventDocument = new PermalinkEventDocument(
            operationId,
            operationId,
            [],
            request.PublishAlias ? "mint" : "identity_seed",
            ids,
            null,
            null,
            providerClaims,
            activeExternalAliases,
            request.ContentRoot,
            request.Leaves,
            bindingId,
            new PermalinkPathAssignment(pathEventId, null, request.Path, root),
            createdAt);
        var anchor = new PermalinkAnchorDocument(
            anchorToken,
            capsuleId,
            bindingId,
            operationId,
            request.ItemKind,
            request.ContentRoot,
            request.Path,
            root,
            pathEventId,
            createdAt);
        return new PermalinkGenesisCandidate(
            anchorToken,
            capsuleId,
            bindingId,
            root,
            capsulePath,
            request.ItemKind,
            request.ContentRoot,
            request.Path,
            CanonicalJson.Serialize(header),
            CanonicalJson.Serialize(eventDocument),
            CanonicalJson.Serialize(anchor),
            eventDocument,
            issuance,
            createdAt);
    }

    /// <summary>
    /// Constructs an append-only strict-superset content successor.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEventDocument CreateContentSuccessor(
        PermalinkStoreRequest request,
        PermalinkCapsuleSnapshot snapshot)
    {
        return new PermalinkEventDocument(
            NewGuid(),
            NewGuid(),
            [snapshot.ContentHead.EventId],
            "append-only-discovery",
            [],
            snapshot.ContentHead.EventId,
            snapshot.ContentHead.PreviousAssignmentEventId,
            snapshot.ContentHead.AcceptedProviderClaim,
            snapshot.ContentHead.ActiveExternalAliases,
            request.ContentRoot,
            request.Leaves,
            snapshot.ContentHead.BindingInstanceId,
            snapshot.ContentHead.PathAssignment,
            UtcNow());
    }

    /// <summary>
    /// Constructs the exact content successor owned by a prepared mutation.
    /// </summary>
    /// <param name="request">The mutation request.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEventDocument CreateControlledContentSuccessor(
        PermalinkStoreRequest request,
        PermalinkCapsuleSnapshot snapshot,
        Guid operationId)
    {
        return new PermalinkEventDocument(
            NewGuid(),
            operationId,
            [snapshot.ContentHead.EventId],
            "controlled-mutation",
            [],
            snapshot.ContentHead.EventId,
            snapshot.ContentHead.PreviousAssignmentEventId,
            snapshot.ContentHead.AcceptedProviderClaim,
            snapshot.ContentHead.ActiveExternalAliases,
            request.ContentRoot,
            request.Leaves,
            snapshot.ContentHead.BindingInstanceId,
            snapshot.ContentHead.PathAssignment,
            UtcNow());
    }

    /// <summary>
    /// Constructs one first-alias successor for a seeded capsule.
    /// </summary>
    /// <param name="reservation">The reservation.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="issuance">The issuance.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEventDocument CreateAliasSuccessor(
        PermalinkGenesisReservation reservation,
        PermalinkCapsuleSnapshot snapshot,
        PermalinkIssuance issuance)
    {
        return new PermalinkEventDocument(
            NewGuid(),
            NewGuid(),
            [snapshot.ContentHead.EventId],
            "alias_add",
            [new PermalinkEventAlias(issuance.Id, issuance.IssuanceNonce, issuance.CreatedAt)],
            null,
            null,
            snapshot.ContentHead.AcceptedProviderClaim,
            snapshot.ContentHead.ActiveExternalAliases,
            snapshot.ContentHead.ContentRoot,
            snapshot.ContentHead.Leaves,
            reservation.BindingId,
            snapshot.ContentHead.PathAssignment,
            UtcNow());
    }

    /// <summary>
    /// Constructs one accepted-provider alias successor for a seeded capsule.
    /// </summary>
    /// <param name="reservation">The reservation.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="alias">The alias.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEventDocument CreateExternalAliasSuccessor(
        PermalinkGenesisReservation reservation,
        PermalinkCapsuleSnapshot snapshot,
        string alias)
    {
        return new PermalinkEventDocument(
            NewGuid(),
            NewGuid(),
            [snapshot.ContentHead.EventId],
            "alias_add",
            [],
            null,
            null,
            [ToProviderClaim(alias)],
            [alias],
            snapshot.ContentHead.ContentRoot,
            snapshot.ContentHead.Leaves,
            reservation.BindingId,
            snapshot.ContentHead.PathAssignment,
            UtcNow());
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static Guid NewGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    private static PermalinkProviderClaim ToProviderClaim(string alias)
    {
        if (alias.StartsWith("tt", StringComparison.Ordinal))
        {
            return new PermalinkProviderClaim("imdb", alias);
        }

        var value = alias[(alias.LastIndexOf('-') + 1)..];
        return new PermalinkProviderClaim("tmdb", value);
    }
}
