// SlopTank modification notice: added or changed by SlopTank on 2026-10-07, 2026-10-08.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Coordinates linked-child database writes with durable aggregate successors.</summary>
public interface IPermalinkAggregateMutationAdapter
{
    /// <summary>Executes one database mutation after freezing every affected aggregate outcome.</summary>
    /// <param name="changes">The complete old and desired membership sets.</param>
    /// <param name="mutation">The item-database mutation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ExecuteAsync(
        IReadOnlyList<PermalinkAggregateMembershipChange> changes,
        Func<Task> mutation,
        CancellationToken cancellationToken);

    /// <summary>Recovers one interrupted aggregate membership mutation from durable evidence.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The settled operation state.</returns>
    Task<PermalinkMutationResult> RecoverAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies from durable records that an aggregate operation holding no phase evidence took
    /// no effect, so a nothing-happened terminal would be truthful.
    /// </summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the verification; refusals throw.</returns>
    Task VerifyNeverPreparedAsync(Guid operationId, CancellationToken cancellationToken);
}

/// <summary>Complete membership state for one aggregate database mutation.</summary>
/// <param name="Aggregate">The affected aggregate.</param>
/// <param name="PreviousMembers">The exact members currently persisted.</param>
/// <param name="DesiredMembers">The exact members the mutation will persist.</param>
public sealed record PermalinkAggregateMembershipChange(
    BaseItem Aggregate,
    IReadOnlyList<BaseItem> PreviousMembers,
    IReadOnlyList<BaseItem> DesiredMembers);
