using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Persists deterministic immutable phases for protected permalink mutations.
/// </summary>
internal sealed class PermalinkOperationJournal : IDisposable
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly SemaphoreSlim _pendingIndexLock = new(1, 1);
    private Dictionary<Guid, PermalinkOperationDocument>? _pendingOperations;

    public PermalinkOperationJournal(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _authority = authority;
        _fileSystem = fileSystem;
    }

    public string GetOperationPath(Guid operationId)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalinks",
            "operations",
            operationId.ToString("D"));
    }

    /// <summary>
    /// Returns the scan-excluded operation area on the affected content filesystem.
    /// </summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <returns>The resulting value.</returns>
    public string GetContentOperationPath(PermalinkOperationDocument operation)
    {
        var root = operation.Bundle?.ContentRootPath
            ?? Path.GetPathRoot(operation.SourcePath)
            ?? throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "operation-root-missing",
                $"Operation '{operation.OperationId}' has no content filesystem root.");
        return Path.Combine(
            root,
            ".sloptank",
            "permalink-operations",
            operation.OperationId.ToString("D"));
    }

    public async Task<PermalinkOperationDocument?> ReadAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetOperationPath(operationId), "operation.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return CanonicalJson.Deserialize<PermalinkOperationDocument>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
    }

    public async Task WriteOperationAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var root = GetOperationPath(operation.OperationId);
        _fileSystem.CreateDirectoryDurable(root);
        await PublishExactAsync(
            Path.Combine(root, "operation.json"),
            CanonicalJson.Serialize(operation),
            cancellationToken).ConfigureAwait(false);
        await _pendingIndexLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_pendingOperations is not null)
            {
                if (GetSettledPhase(operation.OperationId) is null)
                {
                    _pendingOperations[operation.OperationId] = operation;
                }
                else
                {
                    _pendingOperations.Remove(operation.OperationId);
                }
            }
        }
        finally
        {
            _pendingIndexLock.Release();
        }
    }

    public async Task WritePhaseAsync(
        Guid operationId,
        PermalinkPhase phase,
        PermalinkOperationPhase document,
        CancellationToken cancellationToken)
    {
        await PublishExactAsync(
            Path.Combine(GetOperationPath(operationId), phase.Name + ".json"),
            CanonicalJson.Serialize(document),
            cancellationToken).ConfigureAwait(false);
        if (phase.FencesItem)
        {
            return;
        }

        await _pendingIndexLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _pendingOperations?.Remove(operationId);
        }
        finally
        {
            _pendingIndexLock.Release();
        }
    }

    public bool HasPhase(Guid operationId, PermalinkPhase phase)
    {
        return File.Exists(Path.Combine(GetOperationPath(operationId), phase.Name + ".json"));
    }

    /// <summary>
    /// Returns the durable phase that stopped this operation, or null while work remains pending.
    /// </summary>
    /// <remarks>
    /// This is the one predicate behind both the item fence and the automatic-progress gate. It
    /// answers "may anything still advance this operation on its own", which a reverted operation
    /// answers no to just as a terminal one does, even though a reverted operation is still
    /// finishable by an administrator.
    /// </remarks>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The settled phase when one exists.</returns>
    public PermalinkPhase? GetSettledPhase(Guid operationId)
    {
        return PermalinkPhase.Declared.FirstOrDefault(
            phase => !phase.FencesItem && HasPhase(operationId, phase));
    }

    /// <summary>Returns the durable terminal phase, or null while the operation can still finish.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The terminal phase when one exists.</returns>
    public PermalinkPhase? GetTerminalPhase(Guid operationId)
    {
        return PermalinkPhase.Declared.FirstOrDefault(
            phase => phase.Disposition == PermalinkPhaseDisposition.Terminal
                && HasPhase(operationId, phase));
    }

    /// <summary>Reads and validates one deterministic phase when it exists.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="phase">The phase.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkOperationPhase?> ReadPhaseAsync(
        Guid operationId,
        PermalinkPhase phase,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetOperationPath(operationId), phase.Name + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        return CanonicalJson.Deserialize<PermalinkOperationPhase>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
    }

    /// <summary>Appends one immutable administrator resolution audit record.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="resolution">The resolution.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task WriteResolutionAsync(
        Guid operationId,
        PermalinkOperationResolution resolution,
        CancellationToken cancellationToken)
    {
        var root = GetOperationPath(operationId);
        var ordinal = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "resolution-*.json").Count() + 1
            : 1;
        await PublishExactAsync(
            Path.Combine(root, $"resolution-{ordinal}.json"),
            CanonicalJson.Serialize(resolution),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PermalinkOperationDocument?> FindLatestPendingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await _pendingIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingOperations ??= await LoadPendingOperationsAsync(cancellationToken).ConfigureAwait(false);
            return _pendingOperations.Values
                .Where(candidate => candidate.ItemId.Equals(itemId))
                .MaxBy(candidate => candidate.CreatedAt, StringComparer.Ordinal);
        }
        finally
        {
            _pendingIndexLock.Release();
        }
    }

    private async Task<Dictionary<Guid, PermalinkOperationDocument>> LoadPendingOperationsAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var pending = new Dictionary<Guid, PermalinkOperationDocument>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationIdText = Path.GetFileName(directory);
            if (!Guid.TryParse(operationIdText, out var operationId)
                || GetSettledPhase(operationId) is not null)
            {
                continue;
            }

            var candidate = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                pending[operationId] = candidate;
            }
        }

        return pending;
    }

    /// <summary>Returns whether any non-terminal durable operation fences an item.</summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<bool> HasPendingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        return await FindLatestPendingAsync(itemId, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pendingIndexLock.Dispose();
    }

    private async Task PublishExactAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            try
            {
                await _fileSystem.PublishImmutableAsync(path, bytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
            {
                // The exact-byte comparison below resolves the concurrent publication.
            }
        }

        var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!existing.AsSpan().SequenceEqual(bytes.Span))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-id-reused",
                $"Operation journal '{path}' already contains different immutable input.");
        }
    }
}
