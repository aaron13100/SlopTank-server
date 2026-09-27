// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered through private PermalinkRemovalControllerTests.cs

/// <summary>Implements prepare, commit, and recovery for deliberate file removal.</summary>
internal sealed class PermalinkRemovalCoordinator : IPermalinkRemovalCoordinator
{
    private const int OperationGateCount = 64;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkRemovalEvidenceVerifier _verifier;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim[] _operationGates = Enumerable.Range(0, OperationGateCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public PermalinkRemovalCoordinator(
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        PermalinkRemovalEvidenceVerifier verifier,
        TimeProvider timeProvider)
    {
        _journal = journal;
        _bindings = bindings;
        _verifier = verifier;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<PermalinkRemovalResult> PrepareAsync(
        BaseItem item,
        PermalinkRemovalPrepareRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await _journal.ReadAsync(request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateReplay(existing, request);
            return Result(existing, GetCurrentState(existing.OperationId));
        }

        var intent = await _verifier.FreezeAsync(item, request, cancellationToken)
            .ConfigureAwait(false);
        var operation = new PermalinkOperationDocument(
            request.OperationId,
            item.Id,
            intent.SourceCapsuleId,
            "removal",
            item.Path,
            request.DestinationPath,
            intent.SourceContentRoot,
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            PermalinkIdentitySnapshot.Capture(item),
            null,
            UtcNow())
        {
            Version = 3,
            Removal = intent
        };
        await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operation.OperationId) is { } settled)
        {
            return Result(operation, settled.Name);
        }

        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Prepared, cancellationToken)
            .ConfigureAwait(false);
        return Result(operation, PermalinkPhase.Prepared.Name);
    }

    /// <inheritdoc />
    public Task<PermalinkRemovalResult> CommitAsync(
        Guid operationId,
        PermalinkRemovalCommitRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => CommitCoreAsync(operationId, request, cancellationToken));
    }

    /// <inheritdoc />
    public Task<PermalinkRemovalResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => RecoverCoreAsync(operationId, cancellationToken));
    }

    private async Task<PermalinkRemovalResult> CommitCoreAsync(
        Guid operationId,
        PermalinkRemovalCommitRequest request,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        var intent = operation.Removal!;
        if (!request.ItemId.Equals(operation.ItemId)
            || !request.SurvivorItemId.Equals(intent.SurvivorItemId)
            || !string.Equals(request.DestinationPath, operation.DestinationPath, StringComparison.Ordinal))
        {
            throw Conflict("removal-commit-input", "Commit does not exactly match prepared intent.");
        }

        if (_journal.GetSettledPhase(operationId) is { } settled)
        {
            return Result(operation, settled.Name);
        }

        await _verifier.VerifyCommitAsync(operation, cancellationToken).ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Ready, cancellationToken)
            .ConfigureAwait(false);
        await _bindings.ApplyRemovalBindingsAsync(
            operation.ItemId,
            intent.SurvivorItemId,
            intent.SurvivorCapsuleId,
            intent.SurvivorContentRoot,
            intent.SourceAliases,
            cancellationToken).ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Published, cancellationToken)
            .ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Committed, cancellationToken)
            .ConfigureAwait(false);
        return Result(operation, PermalinkPhase.Committed.Name);
    }

    private async Task<PermalinkRemovalResult> RecoverCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settled)
        {
            return Result(operation, settled.Name);
        }

        var sourceState = await _verifier.ClassifyGroupAsync(
            operation,
            operation.SourcePath,
            cancellationToken).ConfigureAwait(false);
        var destinationState = await _verifier.ClassifyGroupAsync(
            operation,
            operation.DestinationPath!,
            cancellationToken).ConfigureAwait(false);
        if (sourceState == PermalinkRemovalGroupState.Complete
            && destinationState == PermalinkRemovalGroupState.Empty)
        {
            await WritePhaseIfMissingAsync(operation, PermalinkPhase.Aborted, cancellationToken)
                .ConfigureAwait(false);
            return Result(operation, PermalinkPhase.Aborted.Name);
        }

        if (sourceState == PermalinkRemovalGroupState.Empty
            && destinationState == PermalinkRemovalGroupState.Complete)
        {
            return await CommitCoreAsync(
                operationId,
                new PermalinkRemovalCommitRequest(
                    operation.ItemId,
                    operation.DestinationPath!,
                    operation.Removal!.SurvivorItemId),
                cancellationToken).ConfigureAwait(false);
        }

        await WritePhaseIfMissingAsync(operation, PermalinkPhase.ManualIntervention, cancellationToken)
            .ConfigureAwait(false);
        throw Conflict(
            "removal-recovery-ambiguous",
            "Removal recovery found a split, occupied, substituted, doubled, or missing group and will not guess.");
    }

    private async Task<PermalinkOperationDocument> RequireOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("operation-missing", $"Operation '{operationId}' does not exist.");
        if (!string.Equals(operation.Kind, "removal", StringComparison.Ordinal)
            || operation.Version < 3
            || operation.Removal is null
            || string.IsNullOrEmpty(operation.DestinationPath))
        {
            throw Conflict("removal-operation-kind", $"Operation '{operationId}' is not a removal.");
        }

        return operation;
    }

    private static void ValidateReplay(
        PermalinkOperationDocument operation,
        PermalinkRemovalPrepareRequest request)
    {
        if (!string.Equals(operation.Kind, "removal", StringComparison.Ordinal)
            || operation.Version < 3
            || operation.Removal is null
            || !operation.ItemId.Equals(request.ItemId)
            || !operation.Removal.SurvivorItemId.Equals(request.SurvivorItemId)
            || !string.Equals(operation.DestinationPath, request.DestinationPath, StringComparison.Ordinal)
            || !CanonicalJson.Serialize(operation.Removal.EquivalenceEvidence).Span.SequenceEqual(
                CanonicalJson.Serialize(request.EquivalenceEvidence).Span))
        {
            throw Conflict(
                "operation-id-reused",
                $"Operation '{request.OperationId}' has different input.");
        }
    }

    private async Task WritePhaseIfMissingAsync(
        PermalinkOperationDocument operation,
        PermalinkPhase phase,
        CancellationToken cancellationToken)
    {
        if (!_journal.HasPhase(operation.OperationId, phase))
        {
            await _journal.WritePhaseAsync(
                operation.OperationId,
                phase,
                new PermalinkOperationPhase(
                    operation.OperationId,
                    phase.Name,
                    operation.OldContentRoot,
                    operation.CreatedAt),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private string GetCurrentState(Guid operationId)
    {
        if (_journal.GetSettledPhase(operationId) is { } settled)
        {
            return settled.Name;
        }

        if (_journal.HasPhase(operationId, PermalinkPhase.ManualIntervention))
        {
            return PermalinkPhase.ManualIntervention.Name;
        }

        if (_journal.HasPhase(operationId, PermalinkPhase.Published))
        {
            return PermalinkPhase.Published.Name;
        }

        return _journal.HasPhase(operationId, PermalinkPhase.Ready)
            ? PermalinkPhase.Ready.Name
            : PermalinkPhase.Prepared.Name;
    }

    private async Task<T> ExecuteSerializedAsync<T>(
        Guid operationId,
        CancellationToken cancellationToken,
        Func<Task<T>> action)
    {
        var gate = _operationGates[(int)((uint)operationId.GetHashCode() % OperationGateCount)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static PermalinkRemovalResult Result(PermalinkOperationDocument operation, string state)
    {
        return new PermalinkRemovalResult(
            operation.OperationId,
            operation.ItemId,
            operation.Removal!.SurvivorItemId,
            operation.DestinationPath!,
            state);
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
