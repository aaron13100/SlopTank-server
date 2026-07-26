using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns recoverable staging, quarantine, publication, and capsule advancement for media bytes.
/// </summary>
internal sealed class PermalinkMediaMutation
{
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    public PermalinkMediaMutation(
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkCapsuleStore capsules,
        PermalinkTransitionStore transitions,
        PermalinkDocumentFactory documents,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
    }

    public async Task<PermalinkMutationResult> CommitAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationCommitRequest request,
        PermalinkOperationPhase readyPhase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StagedPath) || !File.Exists(request.StagedPath))
        {
            throw Conflict("staging-missing", "The exact desired staging file is missing or unreadable.");
        }

        await _authority.ClaimMutationAsync(
            operation.CapsuleId,
            operation.OldContentRoot,
            operation.OperationId,
            cancellationToken).ConfigureAwait(false);
        var operationRoot = _journal.GetOperationPath(operation.OperationId);
        var stagingDirectory = Path.Combine(operationRoot, "staging");
        var quarantineDirectory = Path.Combine(operationRoot, "quarantine");
        _fileSystem.CreateDirectoryDurable(stagingDirectory);
        _fileSystem.CreateDirectoryDurable(quarantineDirectory);
        var staging = Path.Combine(stagingDirectory, "desired");
        var quarantine = Path.Combine(quarantineDirectory, "old");
        if (!File.Exists(staging))
        {
            File.Copy(request.StagedPath, staging);
            var anchor = _fileSystem.ReadAnchorToken(item.Path)
                ?? throw Conflict("anchor-missing", $"Protected item '{item.Id}' lost its stable anchor.");
            _fileSystem.AssignAnchorToken(staging, anchor);
        }

        await _journal.WritePhaseAsync(
            operation.OperationId,
            "ready",
            readyPhase,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(quarantine))
        {
            File.Move(item.Path, quarantine);
        }

        if (!File.Exists(item.Path))
        {
            File.Move(staging, item.Path);
        }

        return await FinalizeAsync(operation, item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PermalinkMutationResult> FinalizeAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var desired = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            "published",
            new PermalinkOperationPhase(
                operation.OperationId,
                "published",
                desired.ContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        await AppendContentAsync(operation, item, desired, cancellationToken).ConfigureAwait(false);
        await _bindings.UpdateContentRootAsync(
            operation.ItemId,
            desired.ContentRoot,
            cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            "committed",
            new PermalinkOperationPhase(
                operation.OperationId,
                "committed",
                desired.ContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, "committed");
    }

    private async Task AppendContentAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkEvidenceResult desired,
        CancellationToken cancellationToken)
    {
        var anchor = _fileSystem.ReadAnchorToken(item.Path)
            ?? throw Conflict("anchor-missing", $"Protected item '{item.Id}' lost its stable anchor.");
        var reservation = await _authority.FindByAnchorAsync(anchor, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict("anchor-missing", $"Protected anchor '{anchor}' is unknown.");
        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            reservation.RootPath,
            item.Path,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        var storeRequest = new PermalinkStoreRequest(
            item.Id,
            reservation.ItemKind,
            item.Path,
            desired.ContentRoot,
            desired.Leaves,
            true,
            null);
        var successor = _documents.CreateControlledContentSuccessor(
            storeRequest,
            snapshot,
            operation.OperationId);
        var bytes = CanonicalJson.Serialize(successor);
        await _transitions.AuthorizeContentTransitionAsync(
            reservation.CapsuleId,
            snapshot.ContentHead.EventId,
            successor,
            bytes,
            cancellationToken).ConfigureAwait(false);
        await _capsules.AppendContentAsync(capsulePath, successor, bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
