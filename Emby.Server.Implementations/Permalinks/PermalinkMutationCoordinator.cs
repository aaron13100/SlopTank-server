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

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Implements Prepare, Commit, and deterministic recovery for protected item mutations.
/// </summary>
internal sealed class PermalinkMutationCoordinator : IPermalinkMutationCoordinator
{
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
    private readonly TimeProvider _timeProvider;

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
    /// <param name="timeProvider">The time provider.</param>
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
        TimeProvider timeProvider)
    {
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
        if (IsDeletionOfVanishedContent(request.Kind, item))
        {
            // Identity must come from what the authority already recorded, not
            // from the item's bytes: this operation exists precisely because
            // those bytes are gone. Recomputing evidence here threw
            // FileNotFoundException out of ValidateChildrenInternal2 and
            // aborted the entire folder scan, so neither the vanished item was
            // removed nor any new sibling added -- one converted file could
            // freeze ingestion for a whole library folder while the scan still
            // reported "Completed" (production, 2026-09-03).
            binding = ResolveVanishedContentBinding(
                item,
                await _bindings.FindItemBindingsAsync(item.Id, cancellationToken)
                    .ConfigureAwait(false));
            oldContentRoot = binding.ContentRoot;
        }
        else
        {
            var ensured = await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
                .ConfigureAwait(false);
            binding = await _bindings.FindResolutionBindingAsync(
                ensured.CanonicalId,
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
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
        }

        if (operation.Kind is "deletion" or "promotion" or "kind-reclassification")
        {
            return await _itemStateMutation.CommitStateOnlyAsync(operation, cancellationToken)
                .ConfigureAwait(false);
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
                => await _pathMutation.CommitAsync(operation, item, cancellationToken)
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
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
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
            return await AbortAsync(operationId, cancellationToken).ConfigureAwait(false);
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
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetSettledPhase(operationId) is { } settledPhase)
        {
            return new PermalinkMutationResult(operationId, settledPhase.Name);
        }

        if (_journal.HasPhase(operationId, PermalinkPhase.Published))
        {
            return new PermalinkMutationResult(operationId, PermalinkPhase.Published.Name);
        }

        await _journal.WritePhaseAsync(
            operationId,
            PermalinkPhase.Aborted,
            Phase(operation, PermalinkPhase.Aborted, operation.OldContentRoot),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operationId, PermalinkPhase.Aborted.Name);
    }

    private async Task<PermalinkOperationDocument> RequireOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("operation-missing", $"Operation '{operationId}' does not exist.");
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
    /// Deliberately narrow. Only a deletion may proceed without content
    /// evidence: every other mutation kind is asserting something about the
    /// item's content and must still be made to prove it. An unreachable file
    /// under any other kind stays an error.
    /// </remarks>
    private static bool IsDeletionOfVanishedContent(string kind, BaseItem item)
    {
        if (!string.Equals(kind, "deletion", StringComparison.Ordinal)
            || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        // Directory check included because Series/Season/BoxSet paths are
        // folders; a present path of either shape means content is reachable
        // and the normal evidence route applies.
        return !File.Exists(item.Path) && !Directory.Exists(item.Path);
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
