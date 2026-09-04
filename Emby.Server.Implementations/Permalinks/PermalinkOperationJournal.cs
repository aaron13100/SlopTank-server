using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Persists deterministic immutable phases for protected permalink mutations.
/// </summary>
internal sealed class PermalinkOperationJournal : IDisposable
{
    /// <summary>
    /// The configuration key that turns the in-memory pending index off.
    ///
    /// It is a measurement control. The negative-control arm of the pending-check
    /// cost measurement needs the same binary to take the walk path on every
    /// lookup, because a timing from a build that never walks only shows that the
    /// current code is fast, not that the index is what made it fast.
    /// </summary>
    public const string DisablePendingIndexKey = "Permalinks:Diagnostics:DisablePendingIndex";

    /// <summary>
    /// How long the pending-check meter waits before it calls a run of lookups
    /// finished and logs its totals.
    ///
    /// The lookups a library scan makes arrive in a dense run, and the gaps
    /// between scans are minutes long, so one flush per run is one line per
    /// scan without the meter having to know what a scan is.
    /// </summary>
    private static readonly TimeSpan _lookupRunGap = TimeSpan.FromSeconds(60);

    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly ILogger<PermalinkOperationJournal> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _pendingIndexEnabled;
    private readonly SemaphoreSlim _pendingIndexLock = new(1, 1);
    private Dictionary<Guid, PermalinkOperationDocument>? _pendingOperations;
    private int _runLookups;
    private int _runWalks;
    private TimeSpan _runLookupTime;
    private DateTimeOffset _runStartedAt;
    private DateTimeOffset _runLastLookupAt;
    private PermalinkJournalWalk? _lastWalk;

    public PermalinkOperationJournal(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<PermalinkOperationJournal> logger)
    {
        _authority = authority;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        _logger = logger;
        _pendingIndexEnabled = !configuration.GetValue(DisablePendingIndexKey, false);
        if (!_pendingIndexEnabled)
        {
            _logger.LogWarning(
                "Permalink pending index is DISABLED by {Key}. Every pending check now walks the "
                + "whole operation journal, which costs a full directory enumeration per call. "
                + "This exists to measure what the index is worth and is not a production setting.",
                DisablePendingIndexKey);
        }
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
        var startedAt = Stopwatch.GetTimestamp();
        await _pendingIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var walked = _pendingOperations is null;
            var pending = _pendingOperations
                ?? await LoadPendingOperationsAsync(cancellationToken).ConfigureAwait(false);
            if (_pendingIndexEnabled)
            {
                _pendingOperations = pending;
            }

            RecordLookup(Stopwatch.GetElapsedTime(startedAt), walked);
            return pending.Values
                .Where(candidate => candidate.ItemId.Equals(itemId))
                .MaxBy(candidate => candidate.CreatedAt, StringComparer.Ordinal);
        }
        finally
        {
            _pendingIndexLock.Release();
        }
    }

    /// <summary>
    /// Adds one completed pending check to the current run, logging the previous
    /// run's totals once this call proves that run is over.
    ///
    /// The caller already holds <see cref="_pendingIndexLock"/>, which is what
    /// makes the plain field arithmetic here safe.
    /// </summary>
    /// <param name="elapsed">What this one lookup cost, lock acquisition included.</param>
    /// <param name="walked">Whether this lookup had to enumerate the journal.</param>
    private void RecordLookup(TimeSpan elapsed, bool walked)
    {
        var now = _timeProvider.GetUtcNow();
        if (_runLookups > 0 && now - _runLastLookupAt > _lookupRunGap)
        {
            FlushLookupRun();
        }

        if (_runLookups == 0)
        {
            _runStartedAt = now;
        }

        _runLookups++;
        _runWalks += walked ? 1 : 0;
        _runLookupTime += elapsed;
        _runLastLookupAt = now;
    }

    /// <summary>Logs and clears the totals of one run of pending checks.</summary>
    private void FlushLookupRun()
    {
        if (_runLookups == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Permalink pending checks: {Lookups} lookups costing {LookupMs:F1} ms in total "
            + "({Walks} journal walks) between {RunStartedAt:O} and {RunEndedAt:O}, index {IndexState}.",
            _runLookups,
            _runLookupTime.TotalMilliseconds,
            _runWalks,
            _runStartedAt,
            _runLastLookupAt,
            _pendingIndexEnabled ? "enabled" : "disabled");
        _runLookups = 0;
        _runWalks = 0;
        _runLookupTime = TimeSpan.Zero;
    }

    private async Task<Dictionary<Guid, PermalinkOperationDocument>> LoadPendingOperationsAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var startedAt = Stopwatch.GetTimestamp();
        var directories = 0;
        var pending = new Dictionary<Guid, PermalinkOperationDocument>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directories++;
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

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        _lastWalk = new PermalinkJournalWalk(directories, pending.Count, elapsed);
        _logger.LogInformation(
            "Permalink journal walk: {Directories} operation directories, {Pending} still pending, "
            + "{WalkMs:F1} ms, index {IndexState}.",
            directories,
            pending.Count,
            elapsed.TotalMilliseconds,
            _pendingIndexEnabled ? "enabled" : "disabled");
        return pending;
    }

    /// <summary>
    /// Builds the pending index now and returns the operations it holds.
    ///
    /// The index is otherwise built lazily by the first caller, which puts a
    /// full walk of the journal on whichever user happens to arrive first after
    /// a restart. Measured on production 2026-09-02 with 61,702 operation
    /// directories and 308,504 files: the first permalink resolution after a
    /// boot took 124.66s, the second 0.97s. That is a user opening a watch link
    /// and waiting two minutes for a directory scan they did not ask for.
    ///
    /// The reconciler already walks exactly these directories during startup
    /// and already computes the same settled/pending split, so priming from
    /// there costs one walk rather than two and leaves the request path with
    /// nothing to build.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation ids that are still pending.</returns>
    public async Task<PermalinkPendingIndexPriming> PrimePendingIndexAsync(CancellationToken cancellationToken)
    {
        await _pendingIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastWalk = null;
            var pending = _pendingOperations
                ?? await LoadPendingOperationsAsync(cancellationToken).ConfigureAwait(false);
            if (_pendingIndexEnabled)
            {
                _pendingOperations = pending;
            }

            return new PermalinkPendingIndexPriming(pending.Keys.ToArray(), _lastWalk);
        }
        finally
        {
            _pendingIndexLock.Release();
        }
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
        FlushLookupRun();
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
