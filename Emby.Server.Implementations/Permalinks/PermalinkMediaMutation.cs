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
    private readonly PermalinkMutationClaimStore _claims;

    public PermalinkMediaMutation(
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkCapsuleStore capsules,
        PermalinkTransitionStore transitions,
        PermalinkDocumentFactory documents,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkMutationClaimStore claims)
    {
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _capsules = capsules;
        _transitions = transitions;
        _documents = documents;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _claims = claims;
    }

    public async Task<PermalinkMutationResult> CommitAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var staging = await StageAsync(
            operation,
            item,
            request.StagedPath,
            cancellationToken).ConfigureAwait(false);
        await VerifyPreparedOldAsync(operation, item, claimed: false, cancellationToken)
            .ConfigureAwait(false);
        await _claims.ClaimAsync(RequireBundle(operation), operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        await VerifyPreparedOldAsync(operation, item, claimed: true, cancellationToken)
            .ConfigureAwait(false);
        var quarantine = GetQuarantinePath(operation);
        if (!File.Exists(quarantine))
        {
            File.Move(item.Path, quarantine);
        }

        if (!File.Exists(item.Path))
        {
            File.Move(staging, item.Path);
        }

        _evidence.InvalidateContentDigest(item.Path);
        return await FinalizeAsync(operation, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies caller-owned bytes into durable operation staging and publishes exact Ready evidence.
    /// </summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="source">The source stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<string> StageAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        string? source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw Conflict("staging-missing", "The exact desired staging file is missing or unreadable.");
        }

        var staging = GetStagingPath(operation);
        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(staging)!);
        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(GetQuarantinePath(operation))!);
        var supplied = await _evidence.ComputeMediaReplacementAsync(
            item,
            source,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(staging))
        {
            await CopyExclusiveAsync(source, staging, cancellationToken).ConfigureAwait(false);
            var anchor = _fileSystem.ReadAnchorToken(item.Path)
                ?? throw Conflict("anchor-missing", $"Protected item '{item.Id}' lost its stable anchor.");
            _fileSystem.AssignAnchorToken(staging, anchor);
        }

        var desired = await _evidence.ComputeMediaReplacementAsync(
            item,
            staging,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(supplied.ContentRoot, desired.ContentRoot, StringComparison.Ordinal))
        {
            throw Conflict(
                "staging-evidence-mismatch",
                "Operation staging already contains different desired bytes.");
        }

        var existingReady = await _journal.ReadPhaseAsync(
            operation.OperationId,
            "ready",
            cancellationToken).ConfigureAwait(false);
        if (existingReady is not null)
        {
            if (!string.Equals(
                    existingReady.ContentRoot,
                    desired.ContentRoot,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    "staging-evidence-mismatch",
                    "Restaged bytes do not match the operation's immutable Ready evidence.");
            }

            return staging;
        }

        await _journal.WritePhaseAsync(
            operation.OperationId,
            "ready",
            new PermalinkOperationPhase(
                operation.OperationId,
                "ready",
                desired.ContentRoot,
                operation.CreatedAt,
                desired.Leaves),
            cancellationToken).ConfigureAwait(false);
        return staging;
    }

    /// <summary>Returns the deterministic operation-owned desired staging path.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public string GetStagingPath(PermalinkOperationDocument operation)
    {
        return Path.Combine(_journal.GetContentOperationPath(operation), "staging", "desired");
    }

    /// <summary>Returns the deterministic operation-owned prepared-old quarantine path.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public string GetQuarantinePath(PermalinkOperationDocument operation)
    {
        return Path.Combine(_journal.GetContentOperationPath(operation), "quarantine", "old");
    }

    /// <summary>Returns the exact immutable bundle required by a protected operation.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public static PermalinkMutationBundle RequireBundle(PermalinkOperationDocument operation)
    {
        return operation.Bundle
            ?? new PermalinkMutationBundle(
                [
                    new PermalinkMutationClaim(
                        operation.CapsuleId,
                        operation.ItemId,
                        Guid.Empty,
                        operation.OldContentRoot,
                        "content")
                ],
                [new PermalinkMutationPart("main", operation.SourcePath, operation.DestinationPath)],
                Path.GetDirectoryName(operation.SourcePath)!,
                null);
    }

    public async Task<PermalinkMutationResult> FinalizeAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var desired = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        var ready = await _journal.ReadPhaseAsync(
            operation.OperationId,
            "ready",
            cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("ready-missing", "Operation-owned desired evidence is missing.");
        if (!string.Equals(ready.ContentRoot, desired.ContentRoot, StringComparison.Ordinal))
        {
            throw Conflict(
                "desired-evidence-mismatch",
                "Published media does not match the exact operation-owned Ready evidence.");
        }

        await _journal.WritePhaseAsync(
            operation.OperationId,
            "published",
            new PermalinkOperationPhase(
                operation.OperationId,
                "published",
                desired.ContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        await _claims.FinalizeAsync(
            RequireBundle(operation),
            operation.OperationId,
            "ready",
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

    private async Task VerifyPreparedOldAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        bool claimed,
        CancellationToken cancellationToken)
    {
        var current = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal))
        {
            if (claimed)
            {
                await _journal.WritePhaseAsync(
                    operation.OperationId,
                    "claimed_pending",
                    new PermalinkOperationPhase(
                        operation.OperationId,
                        "claimed_pending",
                        current.ContentRoot,
                        operation.CreatedAt,
                        current.Leaves),
                    cancellationToken).ConfigureAwait(false);
            }

            throw Conflict(
                "prepared-old-mismatch",
                "Live content no longer matches prepared-old evidence.");
        }
    }

    private async Task CopyExclusiveAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(
                         source,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _fileSystem.SyncFile(destination);
    }

    private async Task AppendContentAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkEvidenceResult desired,
        CancellationToken cancellationToken)
    {
        var reservation = await _authority.FindByCapsuleAsync(
            operation.CapsuleId,
            cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict(
                "capsule-missing",
                $"Protected capsule '{operation.CapsuleId}' is unknown.");
        var liveAnchor = _fileSystem.ReadAnchorToken(item.Path);
        if (liveAnchor is null)
        {
            _fileSystem.AssignAnchorToken(item.Path, reservation.AnchorToken);
        }
        else if (!string.Equals(liveAnchor, reservation.AnchorToken, StringComparison.Ordinal))
        {
            throw Conflict(
                "anchor-mismatch",
                $"Published media has anchor '{liveAnchor}', not prepared anchor '{reservation.AnchorToken}'.");
        }

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
