// SlopTank modification notice: added or changed by SlopTank on 2026-10-07, 2026-10-08.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Publishes exact BoxSet relation successors around item-database writes.</summary>
internal sealed class PermalinkAggregateMutationAdapter : IPermalinkAggregateMutationAdapter, IDisposable
{
    internal const string FaultAfterMutationKey = "Permalinks:Diagnostics:AggregateFaultAfterMutation";

    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkManager _manager;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkMutationClaimStore _claims;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkTransitionStore _transitions;
    private readonly ILinkedChildrenService _linkedChildren;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PermalinkAggregateMutationAdapter(
        PermalinkAuthorityStore authority,
        IPermalinkManager manager,
        PermalinkBindingIndex bindings,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkEvidence evidence,
        PermalinkMutationClaimStore claims,
        PermalinkOperationJournal journal,
        PermalinkTransitionStore transitions,
        ILinkedChildrenService linkedChildren,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _manager = manager;
        _bindings = bindings;
        _capsules = capsules;
        _documents = documents;
        _evidence = evidence;
        _claims = claims;
        _journal = journal;
        _transitions = transitions;
        _linkedChildren = linkedChildren;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    private enum AggregateMembershipOutcome
    {
        Previous,
        Desired
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public async Task ExecuteAsync(
        IReadOnlyList<PermalinkAggregateMembershipChange> changes,
        Func<Task> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(mutation);
        if (!_authority.IsConfigured || changes.Count == 0)
        {
            await mutation().ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationId = Guid.NewGuid();
            var plans = new List<Plan>();
            foreach (var change in changes.DistinctBy(value => value.Aggregate.Id))
            {
                if (change.Aggregate is not BoxSet boxSet)
                {
                    continue;
                }

                var bindings = await _bindings.FindItemBindingsAsync(boxSet.Id, cancellationToken)
                    .ConfigureAwait(false);
                var identities = bindings.Select(value => (value.CapsuleId, value.ContentRoot)).Distinct().ToArray();
                if (identities.Length == 0)
                {
                    continue;
                }

                if (identities.Length != 1)
                {
                    throw Conflict("aggregate-binding-unknown", $"Aggregate '{boxSet.Id}' has ambiguous bindings.");
                }

                var reservation = await _authority.FindByCapsuleAsync(identities[0].CapsuleId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw Conflict("capsule-missing", $"Aggregate capsule '{identities[0].CapsuleId}' is unknown.");
                var snapshot = await _capsules.ReadValidatedAsync(
                    reservation.CapsulePath,
                    reservation.CapsuleId,
                    reservation.ItemKind,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(snapshot.ContentHead.ContentRoot, identities[0].ContentRoot, StringComparison.Ordinal))
                {
                    throw Conflict("aggregate-head-mismatch", $"Aggregate '{boxSet.Id}' binding is not current.");
                }

                var previous = _evidence.ComputeBoxSetManifest(
                    reservation.CapsuleId,
                    await ResolveMembersAsync(change.PreviousMembers, cancellationToken).ConfigureAwait(false));
                var desired = _evidence.ComputeBoxSetManifest(
                    reservation.CapsuleId,
                    await ResolveMembersAsync(change.DesiredMembers, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(previous.ContentRoot, snapshot.ContentHead.ContentRoot, StringComparison.Ordinal))
                {
                    throw Conflict("aggregate-old-mismatch", $"Aggregate '{boxSet.Id}' old membership is not current.");
                }

                plans.Add(new Plan(
                    boxSet,
                    reservation,
                    snapshot,
                    change.PreviousMembers,
                    change.DesiredMembers,
                    previous,
                    desired,
                    CreatePrepared(operationId, boxSet, reservation, snapshot, desired),
                    CreatePrepared(operationId, boxSet, reservation, snapshot, previous)));
            }

            if (plans.Count == 0)
            {
                await mutation().ConfigureAwait(false);
                return;
            }

            var createdAt = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            var bundle = new PermalinkMutationBundle(
                plans.Select(plan => new PermalinkMutationClaim(
                        plan.Reservation.CapsuleId,
                        plan.Aggregate.Id,
                        plan.Reservation.BindingId,
                        plan.Snapshot.ContentHead.ContentRoot,
                        "aggregate"))
                    .OrderBy(value => value.CapsuleId)
                    .ToArray(),
                [new PermalinkMutationPart("relation", plans[0].Aggregate.Path, null)],
                plans[0].Reservation.RootPath,
                null);
            var membershipPlans = plans.Select(plan => new PermalinkAggregateMembershipPlan(
                    plan.Aggregate.Id,
                    plan.Reservation.CapsuleId,
                    MemberIds(plan.PreviousMembers),
                    MemberIds(plan.DesiredMembers)))
                .OrderBy(value => value.CapsuleId)
                .ToArray();
            var operation = new PermalinkOperationDocument(
                operationId,
                plans[0].Aggregate.Id,
                plans[0].Reservation.CapsuleId,
                "aggregate-membership",
                plans[0].Aggregate.Path,
                null,
                plans[0].Previous.ContentRoot,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null,
                bundle,
                createdAt)
            {
                AggregateMembership = membershipPlans
            };
            await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                PermalinkPhase.Prepared,
                new PermalinkOperationPhase(
                    operationId,
                    PermalinkPhase.Prepared.Name,
                    plans[0].Previous.ContentRoot,
                    createdAt,
                    plans[0].Previous.Leaves),
                cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                PermalinkPhase.Ready,
                new PermalinkOperationPhase(
                    operationId,
                    PermalinkPhase.Ready.Name,
                    plans[0].Desired.ContentRoot,
                    createdAt,
                    plans[0].Desired.Leaves)
                {
                    PreparedSuccessors = plans.Select(value => value.DesiredSuccessor).ToArray(),
                    RollbackSuccessors = plans.Select(value => value.RollbackSuccessor).ToArray()
                },
                cancellationToken).ConfigureAwait(false);
            await _claims.ClaimAsync(bundle, operationId, cancellationToken).ConfigureAwait(false);

            Exception? mutationFailure = null;
            try
            {
                await mutation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                mutationFailure = exception;
            }

            if (mutationFailure is null && _configuration.GetValue(FaultAfterMutationKey, false))
            {
                throw new InvalidOperationException($"Aggregate mutation fault injected by {FaultAfterMutationKey}.");
            }

            AggregateMembershipOutcome outcome;
            try
            {
                outcome = ObserveMembershipOutcome(membershipPlans);
            }
            catch (PermalinkException exception)
            {
                throw new PermalinkException(
                    exception.Kind,
                    exception.Code,
                    exception.Message,
                    mutationFailure ?? exception,
                    operationId,
                    plans[0].Aggregate.Id);
            }

            var selected = outcome == AggregateMembershipOutcome.Desired
                ? plans.Select(value => value.DesiredSuccessor).ToArray()
                : plans.Select(value => value.RollbackSuccessor).ToArray();
            var decision = outcome == AggregateMembershipOutcome.Desired ? "ready" : "cancelled";
            var terminal = outcome == AggregateMembershipOutcome.Desired
                ? PermalinkPhase.Committed
                : PermalinkPhase.Cancelled;
            await _claims.FinalizePreparedAsync(
                bundle,
                operationId,
                selected,
                decision,
                cancellationToken).ConfigureAwait(false);
            await PublishAsync(selected, cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                PermalinkPhase.Published,
                new PermalinkOperationPhase(
                    operationId,
                    PermalinkPhase.Published.Name,
                    selected[0].Event.ContentRoot,
                    createdAt),
                cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                terminal,
                new PermalinkOperationPhase(
                    operationId,
                    terminal.Name,
                    selected[0].Event.ContentRoot,
                    createdAt),
                cancellationToken).ConfigureAwait(false);
            if (mutationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(mutationFailure).Throw();
            }

            if (outcome == AggregateMembershipOutcome.Previous)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "aggregate-mutation-not-applied",
                    "The aggregate database write returned without applying its frozen desired membership.",
                    operationId: operationId,
                    itemId: plans[0].Aggregate.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw Conflict("operation-missing", $"Operation '{operationId}' does not exist.");
            if (!string.Equals(operation.Kind, "aggregate-membership", StringComparison.Ordinal))
            {
                throw Conflict("operation-kind-invalid", $"Operation '{operationId}' is not an aggregate membership mutation.");
            }

            if (_journal.GetSettledPhase(operationId) is { } settled)
            {
                return new PermalinkMutationResult(operationId, settled.Name);
            }

            var membership = operation.AggregateMembership
                ?? throw Conflict("aggregate-recovery-evidence-missing", "Aggregate membership recovery evidence is missing.");
            var ready = await _journal.ReadPhaseAsync(operationId, PermalinkPhase.Ready, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict("ready-missing", "Aggregate successor evidence is missing.");
            if (ready.Version < 3)
            {
                throw Conflict(
                    "aggregate-successor-evidence-invalid",
                    "Aggregate Ready evidence does not declare the frozen successor format.");
            }

            var desired = ValidateSuccessors(operation, ready.PreparedSuccessors, membership, "prepared");
            var rollback = ValidateSuccessors(operation, ready.RollbackSuccessors, membership, "rollback");

            IReadOnlyList<PermalinkPreparedContentSuccessor> selected;
            string decision;
            PermalinkPhase terminal;
            var outcome = ObserveMembershipOutcome(membership);
            if (outcome == AggregateMembershipOutcome.Desired)
            {
                selected = desired;
                decision = "ready";
                terminal = PermalinkPhase.Committed;
            }
            else
            {
                selected = rollback;
                decision = "cancelled";
                terminal = PermalinkPhase.Cancelled;
            }

            var bundle = operation.Bundle
                ?? throw Conflict("mutation-bundle-missing", "Aggregate mutation claims are missing.");
            await _claims.FinalizePreparedAsync(
                bundle,
                operationId,
                selected,
                decision,
                cancellationToken).ConfigureAwait(false);
            await PublishAsync(selected, cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                PermalinkPhase.Published,
                new PermalinkOperationPhase(
                    operationId,
                    PermalinkPhase.Published.Name,
                    selected[0].Event.ContentRoot,
                    operation.CreatedAt),
                cancellationToken).ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operationId,
                terminal,
                new PermalinkOperationPhase(
                    operationId,
                    terminal.Name,
                    selected[0].Event.ContentRoot,
                    operation.CreatedAt),
                cancellationToken).ConfigureAwait(false);
            return new PermalinkMutationResult(operationId, terminal.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<PermalinkPreparedContentSuccessor> ValidateSuccessors(
        PermalinkOperationDocument operation,
        IReadOnlyList<PermalinkPreparedContentSuccessor>? successors,
        IReadOnlyList<PermalinkAggregateMembershipPlan> membership,
        string label)
    {
        if (successors is null || successors.Count != membership.Count)
        {
            throw Conflict(
                "aggregate-successor-evidence-invalid",
                $"The {label} aggregate successor set is incomplete.");
        }

        foreach (var plan in membership)
        {
            var successor = successors.SingleOrDefault(value =>
                value.ItemId.Equals(plan.ItemId) && value.CapsuleId.Equals(plan.CapsuleId));
            var claim = operation.Bundle?.Claims.SingleOrDefault(value =>
                value.ItemId.Equals(plan.ItemId) && value.CapsuleId.Equals(plan.CapsuleId));
            if (successor is null
                || claim is null
                || !successor.Event.OperationId.Equals(operation.OperationId)
                || !string.Equals(successor.Predecessor, claim.Predecessor, StringComparison.Ordinal)
                || !string.Equals(
                    successor.Event.ContentRoot,
                    PermalinkEvidence.BuildManifest(successor.Event.Leaves).ContentRoot,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "aggregate-successor-evidence-invalid",
                    $"The {label} successor for aggregate '{plan.ItemId}' is invalid.");
            }
        }

        return successors;
    }

    private AggregateMembershipOutcome ObserveMembershipOutcome(
        IReadOnlyList<PermalinkAggregateMembershipPlan> membership)
    {
        var matchesPrevious = true;
        var matchesDesired = true;
        foreach (var plan in membership)
        {
            var actual = _linkedChildren.GetLinkedChildrenIds(plan.ItemId)
                .Distinct()
                .Order()
                .ToArray();
            matchesPrevious &= actual.SequenceEqual(plan.PreviousMemberIds.Distinct().Order());
            matchesDesired &= actual.SequenceEqual(plan.DesiredMemberIds.Distinct().Order());
        }

        if (matchesDesired)
        {
            return AggregateMembershipOutcome.Desired;
        }

        if (matchesPrevious)
        {
            return AggregateMembershipOutcome.Previous;
        }

        throw Conflict(
            "aggregate-membership-unknown",
            "Live aggregate membership matches neither frozen operation outcome.");
    }

    private async Task<IReadOnlyList<(MediaBrowser.Controller.Entities.BaseItem Item, PermalinkStoredState State)>> ResolveMembersAsync(
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> members,
        CancellationToken cancellationToken)
    {
        var result = new List<(MediaBrowser.Controller.Entities.BaseItem, PermalinkStoredState)>(members.Count);
        foreach (var member in members.DistinctBy(value => value.Id))
        {
            var bindings = await _bindings.FindItemBindingsAsync(member.Id, cancellationToken).ConfigureAwait(false);
            if (bindings.Count == 0)
            {
                _ = await _manager.EnsurePermalinkIdsAsync(member, cancellationToken).ConfigureAwait(false);
                bindings = await _bindings.FindItemBindingsAsync(member.Id, cancellationToken).ConfigureAwait(false);
            }

            var identities = bindings.Select(value => (value.CapsuleId, value.ContentRoot)).Distinct().ToArray();
            if (identities.Length != 1)
            {
                throw Conflict("aggregate-member-unknown", $"Member '{member.Id}' has no exact durable state.");
            }

            result.Add((member, new PermalinkStoredState(identities[0].CapsuleId, identities[0].ContentRoot, [])));
        }

        return result;
    }

    private PermalinkPreparedContentSuccessor CreatePrepared(
        Guid operationId,
        BoxSet aggregate,
        PermalinkGenesisReservation reservation,
        PermalinkCapsuleSnapshot snapshot,
        PermalinkEvidenceResult evidence)
    {
        var request = new PermalinkStoreRequest(
            aggregate.Id,
            reservation.ItemKind,
            aggregate.Path,
            evidence.ContentRoot,
            evidence.Leaves,
            true,
            null);
        return new PermalinkPreparedContentSuccessor(
            reservation.CapsuleId,
            aggregate.Id,
            snapshot.ContentHead.ContentRoot,
            _documents.CreateControlledContentSuccessor(request, snapshot, operationId));
    }

    private async Task PublishAsync(
        IReadOnlyList<PermalinkPreparedContentSuccessor> successors,
        CancellationToken cancellationToken)
    {
        foreach (var successor in successors.OrderBy(value => value.CapsuleId))
        {
            var reservation = await _authority.FindByCapsuleAsync(successor.CapsuleId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict("capsule-missing", $"Aggregate capsule '{successor.CapsuleId}' is unknown.");
            var snapshot = await _capsules.ReadValidatedAsync(
                reservation.CapsulePath,
                reservation.CapsuleId,
                reservation.ItemKind,
                cancellationToken).ConfigureAwait(false);
            if (snapshot.ContentHead.EventId.Equals(successor.Event.EventId))
            {
                var expectedBytes = CanonicalJson.Serialize(successor.Event);
                await _capsules.VerifyContentEventAsync(
                    reservation.CapsulePath,
                    successor.Event,
                    expectedBytes,
                    cancellationToken).ConfigureAwait(false);
                await _bindings.UpdateContentRootAsync(
                    successor.ItemId,
                    successor.Event.ContentRoot,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(snapshot.ContentHead.ContentRoot, successor.Predecessor, StringComparison.Ordinal))
            {
                throw Conflict(
                    "aggregate-publish-head-mismatch",
                    $"Aggregate capsule '{successor.CapsuleId}' has an unexpected content head.");
            }

            var bytes = CanonicalJson.Serialize(successor.Event);
            await _transitions.AuthorizeContentTransitionAsync(
                successor.CapsuleId,
                snapshot.ContentHead.EventId,
                successor.Event,
                bytes,
                cancellationToken).ConfigureAwait(false);
            await _capsules.AppendContentAsync(
                reservation.CapsulePath,
                successor.Event,
                bytes,
                cancellationToken).ConfigureAwait(false);
            await _bindings.UpdateContentRootAsync(
                successor.ItemId,
                successor.Event.ContentRoot,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private static IReadOnlyList<Guid> MemberIds(
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> members)
        => members.Select(value => value.Id).Distinct().Order().ToArray();

    private sealed record Plan(
        BoxSet Aggregate,
        PermalinkGenesisReservation Reservation,
        PermalinkCapsuleSnapshot Snapshot,
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> PreviousMembers,
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> DesiredMembers,
        PermalinkEvidenceResult Previous,
        PermalinkEvidenceResult Desired,
        PermalinkPreparedContentSuccessor DesiredSuccessor,
        PermalinkPreparedContentSuccessor RollbackSuccessor);
}
