using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Encloses identity-bearing Jellyfin entry points in one durable permalink mutation bundle.
/// </summary>
public interface IPermalinkIdentityMutationAdapter
{
    /// <summary>
    /// Executes one mutation after all protected items have durable prepared state.
    /// </summary>
    Task ExecuteAsync(
        IReadOnlyList<BaseItem> items,
        PermalinkIdentityMutationRequest request,
        Func<IPermalinkMutationAmbientToken, CancellationToken, Task> mutation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the latest pending mutation for an item after verifying or restoring old identity.
    /// </summary>
    Task<PermalinkMutationResult> CancelPendingAsync(
        BaseItem item,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits one exact pending logical assignment after current identity revalidation.
    /// </summary>
    Task<PermalinkMutationResult> CommitPendingAsync(
        BaseItem item,
        Guid operationId,
        IReadOnlyDictionary<string, string> desiredProviderIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable identity mutation intent supplied before live state changes.
/// </summary>
public sealed record PermalinkIdentityMutationRequest(
    string Kind,
    IReadOnlyDictionary<string, string>? DesiredProviderIds = null,
    string? DesiredItemKind = null);

/// <summary>
/// Opaque operation context accepted only while its owning adapter invocation is active.
/// </summary>
public interface IPermalinkMutationAmbientToken
{
    /// <summary>Gets the immutable operation ids prepared for this bundle.</summary>
    IReadOnlyList<Guid> OperationIds { get; }
}
