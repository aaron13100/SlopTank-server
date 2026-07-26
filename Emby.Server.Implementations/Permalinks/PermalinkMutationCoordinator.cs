using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
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
        "logical",
        "alias-import",
        "promotion",
        "grouping",
        "deletion",
        "kind-reclassification"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IPermalinkManager _manager;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkMediaMutation _mediaMutation;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkMutationCoordinator"/> class.
    /// </summary>
    public PermalinkMutationCoordinator(
        ILibraryManager libraryManager,
        IPermalinkManager manager,
        PermalinkEvidence evidence,
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkMediaMutation mediaMutation,
        TimeProvider timeProvider)
    {
        _libraryManager = libraryManager;
        _manager = manager;
        _evidence = evidence;
        _authority = authority;
        _journal = journal;
        _bindings = bindings;
        _fileSystem = fileSystem;
        _mediaMutation = mediaMutation;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> PrepareAsync(
        BaseItem item,
        PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || request.ItemId != item.Id || !_kinds.Contains(request.Kind))
        {
            throw Conflict("mutation-input", "Mutation input has an invalid operation, item, or kind.");
        }

        var existing = await _journal.ReadAsync(request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var ensured = await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
            .ConfigureAwait(false);
        var evidence = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        var capsuleId = await _bindings.FindCapsuleIdAsync(
            ensured.CanonicalId,
            item.Id,
            cancellationToken).ConfigureAwait(false);
        var operation = new PermalinkOperationDocument(
            request.OperationId,
            item.Id,
            capsuleId,
            request.Kind,
            item.Path,
            request.DestinationPath,
            evidence.ContentRoot,
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            request.DesiredProviderIds is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(request.DesiredProviderIds, StringComparer.OrdinalIgnoreCase),
            UtcNow());
        if (existing is not null && existing != operation)
        {
            throw Conflict("operation-id-reused", $"Operation '{request.OperationId}' has different input.");
        }

        await _journal.WriteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            request.OperationId,
            "prepared",
            Phase(request.OperationId, "prepared", evidence.ContentRoot),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(request.OperationId, "prepared");
    }

    /// <inheritdoc />
    public async Task<PermalinkMutationResult> CommitAsync(
        Guid operationId,
        PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        var operation = await RequireOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (_journal.HasPhase(operationId, "committed"))
        {
            return new PermalinkMutationResult(operationId, "committed");
        }

        var item = RequireItem(operation.ItemId);
        var current = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal))
        {
            throw Conflict("prepared-old-mismatch", "Live content no longer matches prepared-old evidence.");
        }

        return operation.Kind switch
        {
            "media" => await _mediaMutation.CommitAsync(
                operation,
                item,
                request,
                Phase(operation.OperationId, "ready", null),
                cancellationToken)
                .ConfigureAwait(false),
            "path" or "cross-root" => await CommitPathAsync(operation, item, cancellationToken)
                .ConfigureAwait(false),
            "logical" => await CommitLogicalAsync(operation, item, cancellationToken)
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
        if (_journal.HasPhase(operationId, "committed"))
        {
            return new PermalinkMutationResult(operationId, "committed");
        }

        var item = RequireItem(operation.ItemId);
        var staging = Path.Combine(_journal.GetOperationPath(operationId), "staging", "desired");
        var quarantine = Path.Combine(_journal.GetOperationPath(operationId), "quarantine", "old");
        if (operation.Kind == "media" && !File.Exists(item.Path)
            && File.Exists(staging) && File.Exists(quarantine))
        {
            File.Move(staging, item.Path);
            return await _mediaMutation.FinalizeAsync(operation, item, cancellationToken)
                .ConfigureAwait(false);
        }

        throw Conflict(
            "manual-intervention",
            $"Operation '{operationId}' evidence does not match an automatic recovery branch.");
    }

    private async Task<PermalinkMutationResult> CommitPathAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.DestinationPath)
            || File.Exists(operation.DestinationPath)
            || Directory.Exists(operation.DestinationPath))
        {
            throw Conflict("destination-occupied", "The prepared destination is missing or occupied.");
        }

        _ = Path.GetDirectoryName(operation.DestinationPath) is { } parent
            ? Directory.CreateDirectory(parent)
            : null;
        File.Move(item.Path, operation.DestinationPath);
        item.Path = operation.DestinationPath;
        await _libraryManager.UpdateItemAsync(
            item,
            item.GetParent()!,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);
        var anchor = _fileSystem.ReadAnchorToken(item.Path)
            ?? throw Conflict("anchor-missing", "Moved item lost its stable anchor.");
        await _bindings.UpdateCurrentPathAsync(anchor, item.Path, cancellationToken)
            .ConfigureAwait(false);
        await WritePublishedAndCommittedAsync(operation, operation.OldContentRoot, cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, "committed");
    }

    private async Task<PermalinkMutationResult> CommitLogicalAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (!ProviderIdsEqual(item.ProviderIds, operation.DesiredProviderIds))
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
