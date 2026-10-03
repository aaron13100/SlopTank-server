// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-08-29, 2026-08-30, 2026-09-02, 2026-09-03, 2026-09-08, 2026-09-09, 2026-09-14, 2026-09-20, 2026-09-27, 2026-09-30, 2026-10-03.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Implements Prepare, Commit, and deterministic recovery for protected item mutations.
/// </summary>
internal sealed class PermalinkMutationCoordinator : IPermalinkMutationCoordinator
{
    private const int OperationGateCount = 64;

    private static readonly HashSet<string> _kinds = new(StringComparer.Ordinal)
    {
        "media",
        "path",
        "cross-root",
        "tree",
        "multipart",
        "logical",
        "aggregate",
        "alias-import",
        "promotion",
        "grouping",
        "deletion",
        "kind-reclassification",
        "identify",
        "manual-metadata",
        "automatic-refresh",
        "nfo-refresh",
        "scan-replacement"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IPermalinkManager _manager;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkMutationBundleFactory _bundleFactory;
    private readonly PermalinkMediaMutation _mediaMutation;
    private readonly PermalinkMediaRecovery _mediaRecovery;
    private readonly PermalinkPathMutation _pathMutation;
    private readonly PermalinkItemStateMutation _itemStateMutation;
    private readonly PermalinkContentAdoption _contentAdoption;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PermalinkMutationCoordinator> _logger;
    private readonly SemaphoreSlim[] _operationGates = Enumerable.Range(0, OperationGateCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkMutationCoordinator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="manager">The manager.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="journal">The journal.</param>
    /// <param name="bindings">The bindings.</param>
    /// <param name="fileSystem">The durable permalink filesystem.</param>
    /// <param name="bundleFactory">The bundle factory.</param>
    /// <param name="mediaMutation">The media mutation.</param>
    /// <param name="mediaRecovery">The media recovery.</param>
    /// <param name="pathMutation">The path mutation.</param>
    /// <param name="itemStateMutation">The journal-only item-state mutation service.</param>
    /// <param name="contentAdoption">The elevated leaf content adoption service.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public PermalinkMutationCoordinator(
        ILibraryManager libraryManager,
        IPermalinkManager manager,
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkMutationBundleFactory bundleFactory,
        PermalinkMediaMutation mediaMutation,
        PermalinkMediaRecovery mediaRecovery,
        PermalinkPathMutation pathMutation,
        PermalinkItemStateMutation itemStateMutation,
        PermalinkContentAdoption contentAdoption,
        TimeProvider timeProvider,
        ILogger<PermalinkMutationCoordinator> logger)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _manager = manager;
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _bundleFactory = bundleFactory;
        _mediaMutation = mediaMutation;
        _mediaRecovery = mediaRecovery;
        _pathMutation = pathMutation;
        _itemStateMutation = itemStateMutation;
        _contentAdoption = contentAdoption;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> PrepareAsync(
        BaseItem item,
        PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId.Equals(Guid.Empty)
            || !request.ItemId.Equals(item.Id)
            || !_kinds.Contains(request.Kind))
        {
            throw Conflict("mutation-input", "Mutation input has an invalid operation, item, or kind.");
        }

        var existing = await _journal.ReadAsync(request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        PermalinkResolutionBinding binding;
        string oldContentRoot;
        // A phantom shell's anchor is a random token no authority row knows, so the
        // ensure step would fence the delete before the vanished route is reached.
        var ensuredIdentity = PermalinkCapsuleShell.IsPhantomShell(item)
            ? null
            : await TryEnsureIdentityForDeletionAsync(request, item, cancellationToken)
                .ConfigureAwait(false);
        if (IsDeletionOfVanishedContent(request.Kind, item)
            || ensuredIdentity is null)
        {
            // Identity must come from what the authority already recorded, not
            // from the item's bytes: this operation exists precisely because
            // those bytes are gone. Recomputing evidence here threw
            // FileNotFoundException out of ValidateChildrenInternal2 and
            // aborted the entire folder scan, so neither the vanished item was
            // removed nor any new sibling added -- one converted file could
            // freeze ingestion for a whole library folder while the scan still
            // reported "Completed" (production, 2026-09-03).
            //
            // The same authority-recorded route covers a deletion whose bytes
            // are present but no longer match the capsule (content-mismatch,
            // content-unreachable): a fence whose only complaint is that the
            // content heading out the door changed must not make an item
            // undeletable. Measured in production 2026-09-11: nine fenced
            // items were undeletable from the UI and the endpoint answered a
            // bare 500. Other fences (a pending durable operation, a kind
            // change) still refuse: those are about concurrent authority
            // state, not about the leaving content.
            binding = ResolveVanishedContentBinding(
                item,
                await _bindings.FindItemBindingsAsync(item.Id, cancellationToken)
                    .ConfigureAwait(false));
            oldContentRoot = binding.ContentRoot;
        }
        else
        {
            binding = await _bindings.FindResolutionBindingAsync(
                ensuredIdentity.CanonicalId,
                item.Id,
                cancellationToken).ConfigureAwait(false);
            oldContentRoot = item is Series or Season or BoxSet
                ? binding.ContentRoot
                : (await _evidence.ComputeContentItemAsync(item, cancellationToken)
                    .ConfigureAwait(false)).ContentRoot;
        }

        var capsuleId = binding.CapsuleId;
        var bundle = await _bundleFactory.CreateAsync(item, request, cancellationToken)
            .ConfigureAwait(false);
        var operation = new PermalinkOperationDocument(
            request.OperationId,
            item.Id,
            capsuleId,
            request.Kind,
            item.Path,
            request.DestinationPath,
            oldContentRoot,
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            request.DesiredProviderIds is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(request.DesiredProviderIds, StringComparer.OrdinalIgnoreCase),
            PermalinkIdentitySnapshot.Capture(item),
            bundle,
            UtcNow());
        if (existing is not null
            && !CanonicalJson.Serialize(existing).Span.SequenceEqual(
                CanonicalJson.Serialize(operation with { CreatedAt = existing.CreatedAt }).Span))
        {
            throw Conflict("operation-id-reused", $"Operation '{request.OperationId}' has different input.");
        }

        operation = existing ?? operation;
        await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_journal.HasPhase(request.OperationId, PermalinkPhase.Prepared))
            {
                await _journal.WritePhaseAsync(
                    request.OperationId,
                    PermalinkPhase.Prepared,
                    Phase(operation, PermalinkPhase.Prepared, oldContentRoot),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (existing is null)
            {
                await _journal.WritePhaseAsync(
                    request.OperationId,
                    PermalinkPhase.Aborted,
                    Phase(operation, PermalinkPhase.Aborted, oldContentRoot),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new PermalinkMutationResult(request.OperationId, PermalinkPhase.Prepared.Name);
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CommitAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => CommitCoreAsync(operationId, request, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> CommitCoreAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
        }

        if (string.Equals(operation.Kind, PermalinkAdoptionJournalRecord.OperationKind, StringComparison.Ordinal))
        {
            // Every route settles an interrupted adoption the one way its authority claim allows.
            return await _contentAdoption.RecoverAsync(
                operation,
                RequireItem(operation.ItemId),
                cancellationToken).ConfigureAwait(false);
        }

        if (operation.Kind is "deletion" or "promotion" or "kind-reclassification")
        {
            var result = await _itemStateMutation.CommitStateOnlyAsync(operation, cancellationToken)
                .ConfigureAwait(false);
            if (operation.Kind is "deletion" or "promotion")
            {
                RemoveCapsuleShellDebris(operation.SourcePath);
            }

            return result;
        }

        var item = RequireItem(operation.ItemId);

        return operation.Kind switch
        {
            "media" => await _mediaMutation.CommitAsync(
                operation,
                item,
                request,
                cancellationToken)
                .ConfigureAwait(false),
            "path" or "cross-root" or "tree" or "multipart"
                => await _pathMutation.CommitAsync(operation, item, request.WriterAdmissionId, cancellationToken)
                .ConfigureAwait(false),
            var kind when PermalinkItemStateMutation.IsIdentityKind(kind)
                => await _itemStateMutation.CommitIdentityAsync(operation, item, request, cancellationToken)
                .ConfigureAwait(false),
            _ => await _itemStateMutation.CommitStateOnlyAsync(operation, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => RecoverCoreAsync(operationId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> RecoverCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
        }

        if (string.Equals(operation.Kind, PermalinkAdoptionJournalRecord.OperationKind, StringComparison.Ordinal))
        {
            // Every route settles an interrupted adoption the one way its authority claim allows.
            return await _contentAdoption.RecoverAsync(
                operation,
                RequireItem(operation.ItemId),
                cancellationToken).ConfigureAwait(false);
        }

        if (PermalinkItemStateMutation.IsIdentityKind(operation.Kind))
        {
            return await _itemStateMutation.RecoverIdentityAsync(
                operation,
                RequireItem(operation.ItemId),
                cancellationToken).ConfigureAwait(false);
        }

        if (operation.Kind is "deletion" or "promotion" or "kind-reclassification"
            or "aggregate" or "alias-import" or "grouping")
        {
            return await _itemStateMutation.RecoverStateOnlyAsync(operation, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_journal.HasPhase(operationId, PermalinkPhase.Ready)
            && !_journal.HasPhase(operationId, PermalinkPhase.Published))
        {
            return await AbortCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
        }

        var item = RequireItem(operation.ItemId);
        if (operation.Kind == "media")
        {
            return await _mediaRecovery.RecoverAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        if (operation.Kind is "path" or "cross-root" or "tree" or "multipart")
        {
            return await _pathMutation.RecoverAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        throw Conflict(
            "manual-intervention",
            $"Operation '{operationId}' evidence does not match an automatic recovery branch.");
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> ResolveAsync(
        Guid operationId,
        PermalinkOperationResolutionRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => ResolveCoreAsync(operationId, request, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> ResolveCoreAsync(
        Guid operationId,
        PermalinkOperationResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        // Resolve is the administrator entry point. A reverted operation still holds its
        // reserved transition claim, so a guarded restage must be able to finish this same
        // operation; only a terminal phase closes it here.
        if (_journal.GetTerminalPhase(operationId) is { } terminalPhase)
        {
            return new PermalinkMutationResult(operationId, terminalPhase.Name);
        }

        var item = RequireItem(operation.ItemId);
        return operation.Kind == "media"
            ? await _mediaRecovery.ResolveAsync(operation, item, request, cancellationToken)
                .ConfigureAwait(false)
            : throw Conflict(
                "resolution-action-invalid",
                $"Operation kind '{operation.Kind}' has no guarded '{request.Action}' action.");
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => CancelCoreAsync(operationId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> CancelCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        var settledPhase = _journal.GetSettledPhase(operationId);
        if (settledPhase == PermalinkPhase.Cancelled)
        {
            return new PermalinkMutationResult(operationId, PermalinkPhase.Cancelled.Name);
        }

        if (settledPhase is not null)
        {
            throw Conflict(
                "operation-terminal",
                $"Operation '{operationId}' is already settled as '{settledPhase.Name}'.");
        }

        return await _itemStateMutation.CancelAsync(
            operation,
            RequireItem(operation.ItemId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> AbortAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => AbortCoreAsync(operationId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> AbortCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
        }

        if (string.Equals(operation.Kind, PermalinkAdoptionJournalRecord.OperationKind, StringComparison.Ordinal))
        {
            // Every route settles an interrupted adoption the one way its authority claim allows.
            return await _contentAdoption.RecoverAsync(
                operation,
                RequireItem(operation.ItemId),
                cancellationToken).ConfigureAwait(false);
        }

        if (_journal.HasPhase(operationId, PermalinkPhase.Published))
        {
            return await _itemStateMutation.CloseAssignmentUnknownAsync(
                operation,
                _libraryManager.GetItemById<BaseItem>(operation.ItemId),
                cancellationToken).ConfigureAwait(false);
        }

        await _journal.WritePhaseAsync(
            operationId,
            PermalinkPhase.Aborted,
            Phase(operation, PermalinkPhase.Aborted, operation.OldContentRoot),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operationId, PermalinkPhase.Aborted.Name);
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> AdoptContentAsync(
        BaseItem item,
        PermalinkContentAdoptionRequest request,
        CancellationToken cancellationToken)
    {
        // The operation id is derived from the item and the restated observed root,
        // so the serialization gate and the journal record share one identity no
        // caller ever chose.
        var operationId = PermalinkContentAdoption.DeriveOperationId(
            item.Id,
            request.ObservedContentRoot);
        return await ExecuteSerializedAsync(
            operationId,
            cancellationToken,
            () => _contentAdoption.AdoptContentAsync(
                item,
                request,
                operationId,
                cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> ExecuteSerializedAsync(
        Guid operationId,
        CancellationToken cancellationToken,
        Func<Task<PermalinkMutationResult>> action)
    {
        var operationGate = _operationGates[
            (int)((uint)operationId.GetHashCode() % OperationGateCount)];
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<PermalinkOperationDocument> RequireOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("operation-missing", $"Operation '{operationId}' does not exist.");
        if (string.Equals(operation.Kind, "removal", StringComparison.Ordinal))
        {
            throw Conflict(
                "removal-operation-route",
                $"Operation '{operationId}' must use the removal endpoint.");
        }

        return operation;
    }

    private BaseItem RequireItem(Guid itemId)
    {
        return _libraryManager.GetItemById<BaseItem>(itemId)
            ?? throw Conflict("item-missing", $"Prepared item '{itemId}' no longer exists.");
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

    /// <summary>
    /// Whether this is a deletion of an item whose content is already gone.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Only a removal may proceed without content
    /// evidence: every other mutation kind is asserting something about the
    /// item's content and must still be made to prove it. An unreachable file
    /// under any other kind stays an error.
    ///
    /// "Removal" includes a deletion request that
    /// <see cref="PermalinkIdentityMutationAdapter.ResolveEffectiveKind"/>
    /// reclassified as "promotion": deleting a primary that still has a live
    /// alternate version does not stop being a removal of THIS item's content
    /// just because the adapter also promotes a sibling to replace it. The
    /// item passed in here is always the one being prepared, so a promotion's
    /// vanished-content check still applies to the vanishing primary and not
    /// to the (unrelated, still-present) alternate being promoted.
    /// </remarks>
    private static bool IsDeletionOfVanishedContent(string kind, BaseItem item)
    {
        // Vanished means absent, or a capsule-shell phantom of a file-backed item
        // (PermalinkCapsuleShell). A present file, or a directory of any other
        // shape (Series/Season/BoxSet folders, DVD/BluRay), is reachable content
        // and the normal evidence route applies.
        return IsRemovalKind(kind) && PermalinkCapsuleShell.IsVanishedContent(item);
    }

    /// <summary>
    /// Removes the now-meaningless shell left where a vanished file's path used to be.
    /// </summary>
    /// <param name="itemPath">The removed item's recorded path.</param>
    private void RemoveCapsuleShellDebris(string? itemPath)
    {
        try
        {
            if (PermalinkCapsuleShell.TryRemove(itemPath))
            {
                _logger.LogInformation("Removed capsule-shell phantom at {Path}", itemPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The deletion itself is already committed; leftover debris is
            // re-detected and retried by the next sanctioned delete of this path.
            _logger.LogWarning(exception, "Could not remove capsule-shell phantom at {Path}", itemPath);
        }
    }

    /// <summary>
    /// Whether <paramref name="kind"/> represents an item leaving the
    /// library, either as a literal "deletion" or as the "promotion" a
    /// deletion becomes when <see cref="PermalinkIdentityMutationAdapter"/>
    /// reclassifies it to promote a live alternate version in the deleted
    /// primary's place.
    /// </summary>
    /// <param name="kind">The effective mutation kind.</param>
    /// <returns><see langword="true"/> when the kind is a removal.</returns>
    private static bool IsRemovalKind(string kind)
    {
        return string.Equals(kind, "deletion", StringComparison.Ordinal)
            || string.Equals(kind, "promotion", StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the identity ensure for a removal, tolerating the one fence that
    /// must not block it: present bytes that no longer match the recorded
    /// capsule (content-mismatch, content-unreachable). A removal does
    /// not need a verified content head -- the content is leaving -- so that
    /// fence, which (correctly) blocks re-encodes and metadata edits, routes
    /// the removal onto the authority-recorded identity instead of failing
    /// it. Every other failure propagates.
    ///
    /// This also covers the "promotion" a deletion becomes when the deleted
    /// primary has a live alternate version (see
    /// <see cref="IsDeletionOfVanishedContent"/>): production measured this
    /// exact case (InuYasha, 2026-09-11 -> 2026-09-20) where the vanishing
    /// primary's own identity ensure threw content-unreachable, but this
    /// method's gate checked only the literal string "deletion" -- so a
    /// promotion-reclassified removal fell through to the un-tolerant branch
    /// below and the exception escaped uncaught. Folder.cs's own fence caught
    /// it at the scan level and kept the scan alive, but the item was left
    /// permanently skipped rather than actually removed, because this method
    /// never got the chance to route it onto the authority-recorded identity.
    /// </summary>
    /// <param name="request">The mutation request, for the kind.</param>
    /// <param name="item">The item being removed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ensured identity for non-removals and healthy removals;
    /// null when this removal must take the authority-recorded route.</returns>
    private async Task<PermalinkResponse?> TryEnsureIdentityForDeletionAsync(
        PermalinkMutationPrepareRequest request,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (!IsRemovalKind(request.Kind))
        {
            return await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            return await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PermalinkException exception)
        {
            if (string.Equals(exception.Code, "content-mismatch", StringComparison.Ordinal)
                || string.Equals(exception.Code, "content-unreachable", StringComparison.Ordinal))
            {
                return null;
            }

            throw;
        }
    }

    /// <summary>
    /// Picks the binding to journal a vanished-content deletion against.
    /// </summary>
    /// <remarks>
    /// An item can hold several aliases. They must agree on the capsule for
    /// this to be unambiguous; when they do not, that is a real inconsistency
    /// in the authority and is reported as one rather than resolved by picking
    /// arbitrarily, which would record the deletion against the wrong capsule.
    /// </remarks>
    private static PermalinkResolutionBinding ResolveVanishedContentBinding(
        BaseItem item,
        IReadOnlyList<PermalinkResolutionBinding> bindings)
    {
        if (bindings.Count == 0)
        {
            throw Conflict(
                "binding-missing",
                $"Item '{item.Id}' has no durable binding to delete against.");
        }

        var capsules = bindings.Select(value => value.CapsuleId).Distinct().Count();
        if (capsules > 1)
        {
            throw Conflict(
                "binding-capsule-ambiguous",
                $"Item '{item.Id}' has {capsules} capsules across its aliases; "
                + "a vanished-content deletion cannot choose between them.");
        }

        return bindings[0];
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
