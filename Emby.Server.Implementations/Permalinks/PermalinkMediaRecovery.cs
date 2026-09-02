using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Classifies durable media evidence and owns automatic or administrator-selected recovery.
/// </summary>
internal sealed class PermalinkMediaRecovery
{
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkMutationClaimStore _claims;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkMediaMutation _mutation;
    private readonly TimeProvider _timeProvider;

    public PermalinkMediaRecovery(
        PermalinkEvidence evidence,
        PermalinkOperationJournal journal,
        PermalinkMutationClaimStore claims,
        PermalinkBindingIndex bindings,
        PermalinkMediaMutation mutation,
        TimeProvider timeProvider)
    {
        _evidence = evidence;
        _journal = journal;
        _claims = claims;
        _bindings = bindings;
        _mutation = mutation;
        _timeProvider = timeProvider;
    }

    /// <summary>Converges an interrupted media publication only from exact durable evidence.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationResult> RecoverAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false);
        if (ready?.ContentRoot is null)
        {
            throw Conflict("ready-missing", "Operation-owned desired evidence is missing.");
        }

        var evidence = await ObserveAsync(operation, item, ready.ContentRoot, cancellationToken)
            .ConfigureAwait(false);
        var bundle = PermalinkMediaMutation.RequireBundle(operation);
        if (evidence.LiveIsOld && evidence.StagingIsDesired && !evidence.QuarantineExists)
        {
            await _claims.ClaimAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            File.Move(item.Path, _mutation.GetQuarantinePath(operation));
            File.Move(_mutation.GetStagingPath(operation), item.Path);
            _evidence.InvalidateContentDigest(item.Path);
            return await _mutation.FinalizeAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!evidence.LiveExists && evidence.QuarantineIsOld && evidence.StagingIsDesired)
        {
            await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            File.Move(_mutation.GetStagingPath(operation), item.Path);
            _evidence.InvalidateContentDigest(item.Path);
            return await _mutation.FinalizeAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        if (evidence.LiveIsDesired && evidence.QuarantineIsOld)
        {
            await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            return await _mutation.FinalizeAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteManualInterventionAsync(operation, cancellationToken).ConfigureAwait(false);
        throw Conflict(
            "manual-intervention",
            $"Operation '{operation.OperationId}' has mixed or unrecognized recovery evidence.");
    }

    /// <summary>Executes one authenticated resolution action after exact evidence validation.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="request">The mutation request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationResult> ResolveAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkOperationResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var action = request.Action?.Trim().ToLowerInvariant();
        if (action is not ("restage" or "accept-published" or "restore-old" or "close-unrecoverable"))
        {
            throw Conflict(
                "resolution-action-invalid",
                $"Resolution action '{request.Action}' is not recognized.");
        }

        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            cancellationToken).ConfigureAwait(false);
        var desiredRoot = ready?.ContentRoot;
        var evidence = await ObserveAsync(operation, item, desiredRoot, cancellationToken)
            .ConfigureAwait(false);
        var bundle = PermalinkMediaMutation.RequireBundle(operation);
        PermalinkMutationResult result;
        if (action == "restage")
        {
            if (!evidence.LiveIsOld)
            {
                throw EvidenceConflict(action, "live media does not verify prepared-old.");
            }

            if (string.IsNullOrWhiteSpace(request.StagedPath)
                || !File.Exists(request.StagedPath))
            {
                throw EvidenceConflict(
                    action,
                    "supplied exact desired evidence is missing or unreadable.");
            }

            await _mutation.StageAsync(
                operation,
                item,
                request.StagedPath,
                cancellationToken).ConfigureAwait(false);
            result = await RecoverAsync(operation, item, cancellationToken).ConfigureAwait(false);
        }
        else if (action == "accept-published")
        {
            if (!evidence.LiveIsDesired || !evidence.QuarantineIsOld)
            {
                throw EvidenceConflict(
                    action,
                    "live desired or quarantined prepared-old evidence is missing.");
            }

            await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            result = await _mutation.FinalizeAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (action == "restore-old")
        {
            if (evidence.LiveExists || !evidence.QuarantineIsOld)
            {
                throw EvidenceConflict(
                    action,
                    "the live target is occupied or quarantine is not exact prepared-old.");
            }

            File.Move(_mutation.GetQuarantinePath(operation), item.Path);
            _evidence.InvalidateContentDigest(item.Path);
            await _journal.WritePhaseAsync(
                operation.OperationId,
                PermalinkPhase.Restored,
                Phase(operation, PermalinkPhase.Restored, operation.OldContentRoot),
                cancellationToken).ConfigureAwait(false);
            result = new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Restored.Name);
        }
        else
        {
            if (!await _claims.OwnsAsync(bundle, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false)
                || evidence.LiveIsOld
                || evidence.QuarantineIsOld
                || evidence.LiveIsDesired)
            {
                throw EvidenceConflict(
                    action,
                    "the claim is missing or verified old/desired bytes still exist.");
            }

            await _claims.FinalizeAsync(
                bundle,
                operation.OperationId,
                "detached",
                cancellationToken).ConfigureAwait(false);
            await _bindings.RemoveItemBindingsAsync(operation.ItemId, cancellationToken)
                .ConfigureAwait(false);
            await _journal.WritePhaseAsync(
                operation.OperationId,
                PermalinkPhase.Detached,
                Phase(operation, PermalinkPhase.Detached, contentRoot: null),
                cancellationToken).ConfigureAwait(false);
            result = new PermalinkMutationResult(
                operation.OperationId,
                PermalinkPhase.Detached.Name);
        }

        await _journal.WriteResolutionAsync(
            operation.OperationId,
            new PermalinkOperationResolution(
                operation.OperationId,
                action,
                request.StagedPath,
                evidence.State,
                UtcNow()),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<ObservedEvidence> ObserveAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        string? desiredRoot,
        CancellationToken cancellationToken)
    {
        var live = await TryComputeAsync(item, item.Path, cancellationToken).ConfigureAwait(false);
        var stagingPath = _mutation.GetStagingPath(operation);
        var quarantinePath = _mutation.GetQuarantinePath(operation);
        var staging = await TryComputeAsync(item, stagingPath, cancellationToken).ConfigureAwait(false);
        var quarantine = await TryComputeAsync(item, quarantinePath, cancellationToken)
            .ConfigureAwait(false);
        var liveRoot = live?.ContentRoot;
        var stagingRoot = staging?.ContentRoot;
        var quarantineRoot = quarantine?.ContentRoot;
        return new ObservedEvidence(
            liveRoot,
            stagingRoot,
            quarantineRoot,
            liveRoot is not null,
            quarantineRoot is not null,
            string.Equals(liveRoot, operation.OldContentRoot, StringComparison.Ordinal),
            desiredRoot is not null
                && string.Equals(liveRoot, desiredRoot, StringComparison.Ordinal),
            desiredRoot is not null
                && string.Equals(stagingRoot, desiredRoot, StringComparison.Ordinal),
            string.Equals(quarantineRoot, operation.OldContentRoot, StringComparison.Ordinal));
    }

    private async Task<PermalinkEvidenceResult?> TryComputeAsync(
        BaseItem item,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await _evidence.ComputeMediaReplacementAsync(item, path, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RequireClaimsAsync(
        PermalinkMutationBundle bundle,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!await _claims.OwnsAsync(bundle, operationId, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "claim-owner-mismatch",
                $"Operation '{operationId}' does not own its complete mutation bundle.");
        }
    }

    private Task WriteManualInterventionAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        return _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.ManualIntervention,
            Phase(operation, PermalinkPhase.ManualIntervention, contentRoot: null),
            cancellationToken);
    }

    private static PermalinkOperationPhase Phase(
        PermalinkOperationDocument operation,
        PermalinkPhase phase,
        string? contentRoot)
    {
        return new PermalinkOperationPhase(
            operation.OperationId,
            phase.Name,
            contentRoot,
            operation.CreatedAt);
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static PermalinkException EvidenceConflict(string action, string detail)
    {
        return Conflict(
            "resolution-evidence-mismatch",
            $"Resolution action '{action}' rejected because {detail}");
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    private sealed record ObservedEvidence(
        string? LiveRoot,
        string? StagingRoot,
        string? QuarantineRoot,
        bool LiveExists,
        bool QuarantineExists,
        bool LiveIsOld,
        bool LiveIsDesired,
        bool StagingIsDesired,
        bool QuarantineIsOld)
    {
        public string State =>
            $"live={LiveRoot ?? "missing"};staging={StagingRoot ?? "missing"};"
            + $"quarantine={QuarantineRoot ?? "missing"}";
    }
}
