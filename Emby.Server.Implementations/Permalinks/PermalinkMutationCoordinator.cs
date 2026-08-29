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
        var ensured = await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
            .ConfigureAwait(false);
        var binding = await _bindings.FindResolutionBindingAsync(
            ensured.CanonicalId,
            item.Id,
            cancellationToken).ConfigureAwait(false);
        var oldContentRoot = item is Series or Season or BoxSet
            ? binding.ContentRoot
            : (await _evidence.ComputeContentItemAsync(item, cancellationToken)
                .ConfigureAwait(false)).ContentRoot;
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

        await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        try
        {
            await _journal.WritePhaseAsync(
                request.OperationId,
                "prepared",
                Phase(request.OperationId, "prepared", oldContentRoot),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (existing is null)
            {
                await _journal.WritePhaseAsync(
                    request.OperationId,
                    "aborted",
                    Phase(request.OperationId, "aborted", oldContentRoot),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new PermalinkMutationResult(request.OperationId, "prepared");
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CommitAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetTerminalPhase(operationId) is { } terminalPhase)
        {
            return new PermalinkMutationResult(operationId, terminalPhase);
        }

        if (operation.Kind is "deletion" or "promotion" or "kind-reclassification")
        {
            return await CommitStateOnlyAsync(operation, cancellationToken).ConfigureAwait(false);
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
            "logical" or "identify" or "manual-metadata" or "automatic-refresh" or "nfo-refresh"
                => await CommitLogicalAsync(operation, item, request, cancellationToken)
                .ConfigureAwait(false),
            _ => await CommitStateOnlyAsync(operation, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> RecoverAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetTerminalPhase(operationId) is { } terminalPhase)
        {
            return new PermalinkMutationResult(operationId, terminalPhase);
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
        if (_journal.GetTerminalPhase(operationId) is { } terminalPhase)
        {
            return new PermalinkMutationResult(operationId, terminalPhase);
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
        var terminalPhase = _journal.GetTerminalPhase(operationId);
        if (terminalPhase == "cancelled")
        {
            return new PermalinkMutationResult(operationId, "cancelled");
        }

        if (terminalPhase is not null)
        {
            throw Conflict(
                "operation-terminal",
                $"Operation '{operationId}' is already terminal as '{terminalPhase}'.");
        }

        var item = RequireItem(operation.ItemId);
        var oldIdentity = operation.OldIdentity;
        if (oldIdentity is not null
            ? !oldIdentity.Matches(item)
            : !ProviderIdsEqual(item.ProviderIds, operation.OldProviderIds))
        {
            throw Conflict(
                "prepared-old-mismatch",
                "Live identity does not equal the complete prepared-old assignment.");
        }

        await _journal.WritePhaseAsync(
            operation.OperationId,
            "cancelled",
            Phase(operation.OperationId, "cancelled", operation.OldContentRoot),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operationId, "cancelled");
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> AbortAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.GetTerminalPhase(operationId) is { } terminalPhase)
        {
            return new PermalinkMutationResult(operationId, terminalPhase);
        }

        if (_journal.HasPhase(operationId, "published"))
        {
            return new PermalinkMutationResult(operationId, "published");
        }

        await _journal.WritePhaseAsync(
            operationId,
            "aborted",
            Phase(operationId, "aborted", operation.OldContentRoot),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operationId, "aborted");
    }

    private async Task<PermalinkMutationResult> CommitLogicalAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var desired = request.DesiredProviderIds ?? operation.DesiredProviderIds;
        if (!ProviderIdsEqual(item.ProviderIds, desired))
        {
            throw Conflict("desired-assignment-mismatch", "Live provider assignment does not match desired state.");
        }

        await WritePublishedAndCommittedAsync(operation, operation.OldContentRoot, cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, "committed");
    }

    private async Task<PermalinkMutationResult> CommitStateOnlyAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        await WritePublishedAndCommittedAsync(operation, operation.OldContentRoot, cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, "committed");
    }

    private async Task WritePublishedAndCommittedAsync(
        PermalinkOperationDocument operation,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        await _journal.WritePhaseAsync(
            operation.OperationId,
            "ready",
            Phase(operation.OperationId, "ready", contentRoot),
            cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            "published",
            Phase(operation.OperationId, "published", contentRoot),
            cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            "committed",
            Phase(operation.OperationId, "committed", contentRoot),
            cancellationToken).ConfigureAwait(false);
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

    private PermalinkOperationPhase Phase(Guid operationId, string state, string? contentRoot)
    {
        return new PermalinkOperationPhase(operationId, state, contentRoot, UtcNow());
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool ProviderIdsEqual(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> desired)
    {
        return current.Count == desired.Count
            && current.All(pair => desired.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
