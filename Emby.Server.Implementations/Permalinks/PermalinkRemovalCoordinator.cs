// SlopTank modification notice: added or changed by SlopTank on 2026-09-27, 2026-10-14, 2026-10-31, 2026-11-01, 2026-11-02.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
    private readonly PermalinkMutationBundleFactory? _bundleFactory;
    private readonly IPermalinkManager? _manager;
    private readonly PermalinkTransitionPublisher? _aggregatePublisher;
    private readonly PermalinkMutationClaimStore? _claims;
    private readonly ILibraryManager? _libraryManager;
    private readonly IPermalinkIdentityMutationAdapter? _identityMutationAdapter;
    private readonly SemaphoreSlim[] _operationGates = Enumerable.Range(0, OperationGateCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public PermalinkRemovalCoordinator(
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        PermalinkRemovalEvidenceVerifier verifier,
        TimeProvider timeProvider,
        PermalinkMutationBundleFactory? bundleFactory = null,
        IPermalinkManager? manager = null,
        PermalinkTransitionPublisher? aggregatePublisher = null,
        PermalinkMutationClaimStore? claims = null,
        ILibraryManager? libraryManager = null,
        IPermalinkIdentityMutationAdapter? identityMutationAdapter = null)
    {
        _journal = journal;
        _bindings = bindings;
        _verifier = verifier;
        _timeProvider = timeProvider;
        _bundleFactory = bundleFactory;
        _manager = manager;
        _aggregatePublisher = aggregatePublisher;
        _claims = claims;
        _libraryManager = libraryManager;
        _identityMutationAdapter = identityMutationAdapter;
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
            RequireMetadataCleanupDependencies(existing);
            return Result(existing, GetCurrentState(existing.OperationId));
        }

        var intent = await _verifier.FreezeAsync(item, request, cancellationToken)
            .ConfigureAwait(false);
        PermalinkMutationBundle? bundle = null;
        if (_bundleFactory is not null && _manager is not null && _aggregatePublisher is not null)
        {
            foreach (var parent in _bundleFactory.DiscoverItems(item, "removal")
                         .Where(value => !value.Id.Equals(item.Id))
                         .OrderBy(value => value is MediaBrowser.Controller.Entities.TV.Season ? 0
                             : value is MediaBrowser.Controller.Entities.TV.Series ? 1 : 2)
                         .ThenBy(value => value.Id))
            {
                if (await _bundleFactory.FindReservationAsync(parent, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }

                _ = await _manager.EnsurePermalinkIdsAsync(parent, cancellationToken).ConfigureAwait(false);
            }

            bundle = await _bundleFactory.CreateAsync(
                item,
                new PermalinkMutationPrepareRequest(
                    request.OperationId,
                    item.Id,
                    "removal",
                    null,
                    null),
                cancellationToken).ConfigureAwait(false);
            foreach (var dependency in bundle.Claims.Where(value => value.Kind == "aggregate"))
            {
                await _aggregatePublisher.VerifyDependencyAsync(
                    dependency, intent.SourceCapsuleId, cancellationToken).ConfigureAwait(false);
            }
        }

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
            bundle,
            UtcNow())
        {
            Version = 3,
            Removal = intent
        };
        RequireMetadataCleanupDependencies(operation);
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

        RequireMetadataCleanupDependencies(operation);
        if (_journal.GetSettledPhase(operationId) is { } settled)
        {
            if (settled == PermalinkPhase.Committed)
            {
                await SettleAncestorsAsync(operation, cancellationToken).ConfigureAwait(false);
                await CompleteMetadataCleanupAsync(operation, cancellationToken).ConfigureAwait(false);
            }

            return Result(operation, settled.Name);
        }

        await _verifier.VerifyCommitAsync(operation, cancellationToken).ConfigureAwait(false);
        await AdmitAncestorsAsync(operation, cancellationToken).ConfigureAwait(false);
        await WritePhaseIfMissingAsync(operation, PermalinkPhase.Ready, cancellationToken)
            .ConfigureAwait(false);
        await _bindings.ApplyRemovalBindingsAsync(
            operation.ItemId,
            intent.SurvivorItemId,
            intent.SurvivorCapsuleId,
            intent.SurvivorContentRoot,
            intent.SourceAliases,
            cancellationToken).ConfigureAwait(false);
        await SettleAncestorsAsync(operation, cancellationToken).ConfigureAwait(false);
        if (operation.Bundle is { } bundle)
        {
            await _claims!.FinalizeAsync(
                AggregateBundle(bundle), operation.OperationId, "no_change", cancellationToken).ConfigureAwait(false);
        }

        await CompleteMetadataCleanupAsync(operation, cancellationToken).ConfigureAwait(false);
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
        RequireMetadataCleanupDependencies(operation);
        if (_journal.GetSettledPhase(operationId) is { } settled)
        {
            if (settled == PermalinkPhase.Committed)
            {
                await SettleAncestorsAsync(operation, cancellationToken).ConfigureAwait(false);
                await CompleteMetadataCleanupAsync(operation, cancellationToken).ConfigureAwait(false);
            }

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

    private void RequireMetadataCleanupDependencies(PermalinkOperationDocument operation)
    {
        if (operation.Bundle is not null && (_libraryManager is null || _identityMutationAdapter is null))
        {
            throw Conflict(
                "removal-library-unavailable",
                "Frozen removal publication requires normal guarded library metadata cleanup.");
        }
    }

    private async Task CompleteMetadataCleanupAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        if (_libraryManager is not { } library || _identityMutationAdapter is not { } adapter)
        {
            // Older isolated file-only callers have no frozen library bundle.
            // Frozen operations are refused before any publication without both dependencies.
            return;
        }

        var item = FindMetadataItem(library, operation);
        if (item is null)
        {
            return;
        }

        await RequireUnboundSourceAsync(operation, cancellationToken).ConfigureAwait(false);
        await _verifier.VerifyCommitAsync(operation, cancellationToken).ConfigureAwait(false);
        await adapter.ExecuteAsync(
            [item],
            new PermalinkIdentityMutationRequest("deletion"),
            async (_, token) =>
            {
                // The adapter owns the normal item and version-promotion locks.
                // Re-read inside those locks: checks made before admission cannot authorize
                // deleting a later path assignment, rebound item, or reappeared source file.
                var current = FindMetadataItem(library, operation);
                if (current is null)
                {
                    return;
                }

                await RequireUnboundSourceAsync(operation, token).ConfigureAwait(false);
                await _verifier.VerifyCommitAsync(operation, token).ConfigureAwait(false);
                library.DeleteItem(
                    current,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    });
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static BaseItem? FindMetadataItem(ILibraryManager library, PermalinkOperationDocument operation)
    {
        var item = library.GetItemById<BaseItem>(operation.ItemId);
        if (item is null)
        {
            // A prior cleanup or crash after deletion has already completed this projection.
            return null;
        }

        if (!item.Id.Equals(operation.ItemId)
            || !string.Equals(item.Path, operation.SourcePath, StringComparison.Ordinal)
            || item is not Video { AdditionalParts.Length: 0 }
            || (operation.OldIdentity is { } identity
                && !string.Equals(item.GetType().Name, identity.ItemKind, StringComparison.Ordinal)))
        {
            throw Conflict(
                "removal-source-metadata-changed",
                "The prepared source item no longer has its exact frozen path and video identity.");
        }

        return item;
    }

    private async Task RequireUnboundSourceAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        if (await _bindings.HasItemBindingAsync(operation.ItemId, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "removal-source-rebound",
                "The prepared source acquired a live binding and cannot be removed as retired metadata.");
        }
    }

    private async Task SettleAncestorsAsync(PermalinkOperationDocument operation, CancellationToken cancellationToken)
    {
        if (operation.Bundle is null)
        {
            return;
        }

        if (_aggregatePublisher is null)
        {
            throw Conflict("aggregate-proof-unavailable", "Frozen removal ancestors require authority-backed settlement.");
        }

        await _aggregatePublisher.SettleRemovalAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task AdmitAncestorsAsync(PermalinkOperationDocument operation, CancellationToken cancellationToken)
    {
        if (operation.Bundle is not { } frozen)
        {
            return;
        }

        if (_claims is null || _aggregatePublisher is null)
        {
            throw Conflict("aggregate-proof-unavailable", "Removal publication requires live ancestor admission.");
        }

        var bundle = AggregateBundle(frozen);
        foreach (var dependency in bundle.Claims)
        {
            await _aggregatePublisher.VerifyDependencyAsync(
                dependency, operation.CapsuleId, cancellationToken, operation.OperationId).ConfigureAwait(false);
        }

        await _claims.ClaimAsync(bundle, operation.OperationId, cancellationToken).ConfigureAwait(false);
        foreach (var dependency in bundle.Claims)
        {
            await _aggregatePublisher.VerifyDependencyAsync(
                dependency, operation.CapsuleId, cancellationToken, operation.OperationId).ConfigureAwait(false);
        }

        if (!await _claims.OwnsAsync(bundle, operation.OperationId, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("claim-owner-mismatch", "Removal no longer owns its complete ancestor admission.");
        }
    }

    private static PermalinkMutationBundle AggregateBundle(PermalinkMutationBundle bundle) =>
        bundle with { Claims = bundle.Claims.Where(value => value.Kind == "aggregate").ToArray() };

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
