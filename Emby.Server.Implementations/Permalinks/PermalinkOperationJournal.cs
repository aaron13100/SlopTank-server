using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
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
    /// How many operation directories the one-time pending-set migration verifies between durable
    /// checkpoints. Every commit under <c>synchronous=FULL</c> is a real fsync, so checkpointing every
    /// directory would turn a 36-400s walk into a multi-minute one; checkpointing only at the end
    /// would make an interrupted migration redo the whole walk. This bounds redone work, after a
    /// restart mid-migration, to at most one batch.
    /// </summary>
    public const string PendingMigrationBatchSizeKey = "Permalinks:PendingMigrationBatchSize";

    /// <summary>
    /// A migration-only fault injection point, so a restart-mid-migration can be tested without
    /// waiting for a real crash. Once the one-time migration's directory count first reaches this
    /// value, it throws instead of continuing. It fires at most once per migration (tracked by
    /// comparing against the count already durable when this attempt began), so a resumed attempt
    /// that passes the same threshold again is not re-faulted forever.
    /// </summary>
    public const string PendingMigrationFaultAfterDirectoriesKey =
        "Permalinks:Diagnostics:PendingMigrationFaultAfterDirectories";

    /// <summary>
    /// The time terminal operation records remain in the hot journal before startup moves their
    /// byte-identical directory to the monthly archive.
    /// </summary>
    public const string OperationJournalRetentionKey = "Permalinks:OperationJournalRetention";

    /// <summary>
    /// The hard ceiling on indexed candidates selected and exact operation directories examined by
    /// one automatic archive pass.
    /// </summary>
    public const string OperationJournalArchiveMaximumOperationsPerPassKey =
        "Permalinks:OperationJournalArchiveMaximumOperationsPerPass";

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
    private readonly int _pendingMigrationBatchSize;
    private readonly long _pendingMigrationFaultAfterDirectories;
    private readonly TimeSpan _operationJournalRetention;
    private readonly int _operationJournalArchiveMaximumOperationsPerPass;
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
        _pendingMigrationBatchSize = configuration.GetValue(PendingMigrationBatchSizeKey, 2000);
        _pendingMigrationFaultAfterDirectories = configuration.GetValue(
            PendingMigrationFaultAfterDirectoriesKey,
            0L);
        _operationJournalRetention = configuration.GetValue(
            OperationJournalRetentionKey,
            TimeSpan.Zero);
        if (_operationJournalRetention < TimeSpan.Zero)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "operation-journal-retention-invalid",
                $"{OperationJournalRetentionKey} cannot be negative.");
        }

        _operationJournalArchiveMaximumOperationsPerPass = configuration.GetValue(
            OperationJournalArchiveMaximumOperationsPerPassKey,
            100);
        if (_operationJournalArchiveMaximumOperationsPerPass is <= 0 or > 10_000)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "operation-journal-archive-limit-invalid",
                $"{OperationJournalArchiveMaximumOperationsPerPassKey} must be between 1 and 10000.");
        }

        if (!_pendingIndexEnabled)
        {
            _logger.LogWarning(
                "Permalink pending index is DISABLED by {Key}. Every pending check now walks the "
                + "whole operation journal, which costs a full directory enumeration per call. "
                + "This exists to measure what the index is worth and is not a production setting.",
                DisablePendingIndexKey);
        }

        if (_pendingMigrationFaultAfterDirectories > 0)
        {
            _logger.LogWarning(
                "Permalink pending-set migration will fault after {Directories} directories by "
                + "{Key}. This exists to test restart-mid-migration and is not a production setting.",
                _pendingMigrationFaultAfterDirectories,
                PendingMigrationFaultAfterDirectoriesKey);
        }
    }

    public string GetOperationPath(Guid operationId)
    {
        var active = GetActiveOperationPath(operationId);
        if (IsSymbolicLink(active))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-path-linked",
                $"Operation '{operationId}' is stored through a symbolic link and was refused.");
        }

        if (Directory.Exists(active))
        {
            return active;
        }

        var archiveRoot = GetArchiveRoot();
        if (IsSymbolicLink(archiveRoot))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "operation-archive-linked",
                "The permalink operation archive root is a symbolic link and was refused.");
        }

        if (!Directory.Exists(archiveRoot))
        {
            return active;
        }

        string? found = null;
        foreach (var monthPath in Directory.EnumerateDirectories(archiveRoot))
        {
            var month = Path.GetFileName(monthPath);
            if (!DateTime.TryParseExact(
                    month,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)
                || IsSymbolicLink(monthPath))
            {
                continue;
            }

            var candidate = Path.Combine(monthPath, operationId.ToString("D"));
            if (IsSymbolicLink(candidate))
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "operation-archive-path-linked",
                    $"Archived operation '{operationId}' is stored through a symbolic link and was refused.");
            }

            if (!Directory.Exists(candidate))
            {
                continue;
            }

            if (found is not null)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "operation-archive-ambiguous",
                    $"Operation '{operationId}' exists in more than one archive month.");
            }

            found = candidate;
        }

        return found ?? active;
    }

    private string GetActiveOperationPath(Guid operationId)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalinks",
            "operations",
            operationId.ToString("D"));
    }

    private string GetArchiveRoot()
    {
        return Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations-archive");
    }

    /// <summary>
    /// Returns the durable marker path that makes one operation's pendingness a fact the filesystem
    /// already knows, without enumerating <c>operations/</c> to rediscover it.
    /// </summary>
    /// <remarks>
    /// The marker holds the exact same canonical bytes as <c>operation.json</c>, not a stub, so a
    /// crash between the two publishes never loses the operation: <see cref="LoadPendingOperationsFromMarkersAsync"/>
    /// reconstructs <c>operation.json</c> from the marker rather than guessing the marker is an
    /// orphan and discarding it. That distinction matters because "not there yet" and "never coming"
    /// are indistinguishable from a marker alone.
    /// </remarks>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The marker path.</returns>
    private string GetPendingMarkerPath(Guid operationId)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalinks",
            "pending",
            operationId.ToString("D") + ".json");
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var path = Path.Combine(GetOperationPath(operationId), "operation.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return CanonicalJson.Deserialize<PermalinkOperationDocument>(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                    path);
            }
            catch (Exception exception) when (
                attempt == 0
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Retry the locator once across an atomic hot-to-archive rename.
            }
        }

        return null;
    }

    public async Task WriteOperationAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        // The marker publishes FIRST, with the exact same bytes operation.json is about to get.
        // operation.json's existence is what makes an operation pending (nothing has settled it
        // yet), so the marker must never lag behind it: if this process dies before operation.json
        // is published, the marker alone is enough for LoadPendingOperationsFromMarkersAsync to
        // reconstruct it. The reverse order would let a real pending operation start with no durable
        // record of itself, which is the exact bug this marker exists to prevent.
        var bytes = CanonicalJson.Serialize(operation);
        var markerPath = GetPendingMarkerPath(operation.OperationId);
        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(markerPath)!);
        await PublishExactAsync(markerPath, bytes, cancellationToken).ConfigureAwait(false);

        var root = GetActiveOperationPath(operation.OperationId);
        _fileSystem.CreateDirectoryDurable(root);
        var createdOperation = await PublishExactAsync(
            Path.Combine(root, "operation.json"),
            bytes,
            cancellationToken).ConfigureAwait(false);
        if (createdOperation)
        {
            // Only the publish that actually created operation.json counts a new directory; a
            // concurrent retry that lands on the exact-byte-match branch of PublishExactAsync must
            // not double count the same operation.
            await IncrementDirectoryCounterAsync(cancellationToken).ConfigureAwait(false);
        }

        await _pendingIndexLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_pendingOperations is not null)
            {
                if (GetActiveSettledPhase(operation.OperationId) is null)
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
        if (phase.Disposition == PermalinkPhaseDisposition.Terminal)
        {
            await QueueArchiveCandidateAsync(
                operationId,
                phase,
                document,
                cancellationToken).ConfigureAwait(false);
        }

        await PublishExactAsync(
            Path.Combine(GetActiveOperationPath(operationId), phase.Name + ".json"),
            CanonicalJson.Serialize(document),
            cancellationToken).ConfigureAwait(false);
        if (phase.FencesItem)
        {
            return;
        }

        // Settlement is monotonic: once a durable terminal/reverted phase file exists, this
        // operation can never become pending again, so the marker is safe to remove now. The
        // removal itself does not need the same durability ceremony as its creation: if this
        // delete does not survive a crash, LoadPendingOperationsFromMarkersAsync re-derives the
        // same settled verdict from GetSettledPhase and drops the stale marker again on the next
        // boot. A failure here must not stop the in-memory index update below, since the phase file
        // that actually settles the operation is already durably published by this point.
        try
        {
            var markerPath = GetPendingMarkerPath(operationId);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Could not remove the pending marker for settled operation {OperationId}; it will "
                + "self-heal the next time the pending set is loaded.",
                operationId);
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
        var operationPath = GetOperationPath(operationId);
        if (File.Exists(Path.Combine(operationPath, phase.Name + ".json")))
        {
            return true;
        }

        // Only a hot path can change locations: archived directories are immutable and never move
        // again. Retry the locator once when the first lookup named the hot directory, covering an
        // atomic archive rename between GetOperationPath and File.Exists without rescanning every
        // archive month twice for a genuinely absent phase.
        if (!string.Equals(
                operationPath,
                GetActiveOperationPath(operationId),
                StringComparison.Ordinal))
        {
            return false;
        }

        var afterPossibleArchiveMove = GetOperationPath(operationId);
        return !string.Equals(operationPath, afterPossibleArchiveMove, StringComparison.Ordinal)
            && File.Exists(Path.Combine(afterPossibleArchiveMove, phase.Name + ".json"));
    }

    private bool HasActivePhase(Guid operationId, PermalinkPhase phase)
    {
        return File.Exists(Path.Combine(GetActiveOperationPath(operationId), phase.Name + ".json"));
    }

    private PermalinkPhase? GetActiveSettledPhase(Guid operationId)
    {
        return PermalinkPhase.Declared.FirstOrDefault(
            phase => !phase.FencesItem && HasActivePhase(operationId, phase));
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var path = Path.Combine(GetOperationPath(operationId), phase.Name + ".json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return CanonicalJson.Deserialize<PermalinkOperationPhase>(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                    path);
            }
            catch (Exception exception) when (
                attempt == 0
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Retry the locator once across an atomic hot-to-archive rename.
            }
        }

        return null;
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
        var root = GetActiveOperationPath(operationId);
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

    /// <summary>
    /// Loads the pending set, from a directory listing bounded by the pending count once the
    /// one-time migration has run, or by running that migration now when it has not.
    /// </summary>
    /// <remarks>
    /// "Which operations are still pending" used to be re-derived, on every cold start, by
    /// enumerating every operation directory that ever existed (62,565 of them on 2026-09-04,
    /// growing 1,500-2,800 a day, nothing prunes) and probing up to five phase files in each.
    /// Every sample ever taken found zero pending among them: the server paid O(total history) to
    /// compute O(0). The <c>PermalinkPendingMigration</c> authority table is a one-row durable fact,
    /// seeded once by <see cref="MigratePendingSetAsync"/>, that lets every later boot answer from a
    /// directory listing of <c>pending/</c> instead.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current pending operations, keyed by operation id.</returns>
    private async Task<Dictionary<Guid, PermalinkOperationDocument>> LoadPendingOperationsAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadMigrationStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Completed
            ? await LoadPendingOperationsFromMarkersAsync(cancellationToken).ConfigureAwait(false)
            : await MigratePendingSetAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the durable marker area and self-heals every marker it finds against the operation it
    /// names, rather than trusting the marker's own claim of pendingness.
    /// </summary>
    /// <remarks>
    /// Two kinds of staleness are possible, both harmless if left alone until the next load and
    /// corrected here rather than assumed away:
    /// <list type="bullet">
    /// <item>the marker's operation settled and <see cref="WritePhaseAsync"/>'s best-effort delete
    /// did not survive a crash, so <see cref="GetSettledPhase"/> now disagrees with the marker;
    /// dropping the marker is safe because settlement is monotonic and cannot un-happen;</item>
    /// <item>this process (or a predecessor) died between publishing the marker and publishing
    /// <c>operation.json</c>, so the marker is momentarily the only durable copy; the marker holds
    /// the exact canonical bytes <c>operation.json</c> was about to get, so reconstructing it is
    /// exact-byte republication, not a guess.</item>
    /// </list>
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current pending operations, keyed by operation id.</returns>
    private async Task<Dictionary<Guid, PermalinkOperationDocument>> LoadPendingOperationsFromMarkersAsync(
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var pending = await ComputePendingFromMarkersAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var totalDirectories = await ReadDirectoryCounterAsync(cancellationToken).ConfigureAwait(false);
        _lastWalk = new PermalinkJournalWalk((int)Math.Min(totalDirectories, int.MaxValue), pending.Count, elapsed);
        _logger.LogInformation(
            "Permalink journal walk: {Directories} operation directories, {Pending} still pending, "
            + "{WalkMs:F1} ms, index {IndexState}.",
            totalDirectories,
            pending.Count,
            elapsed.TotalMilliseconds,
            _pendingIndexEnabled ? "enabled" : "disabled");
        return pending;
    }

    /// <summary>
    /// Lists <c>pending/</c> and self-heals every marker it finds against the operation it names.
    /// </summary>
    /// <remarks>
    /// This is the only trustworthy source of "what is pending right now": a resumed
    /// <see cref="MigratePendingSetAsync"/> walk only visits the directories it has not verified yet,
    /// so an operation whose marker an EARLIER, interrupted attempt already published would be
    /// missing from that walk's own in-memory results even though it is genuinely still pending.
    /// Reading it back from here once the walk finishes, rather than trusting what any one attempt's
    /// pass accumulated, is what keeps a multi-boot migration from silently losing an operation that
    /// happened to fall before its resume cursor.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current pending operations, keyed by operation id.</returns>
    private async Task<Dictionary<Guid, PermalinkOperationDocument>> ComputePendingFromMarkersAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "pending");
        var pending = new Dictionary<Guid, PermalinkOperationDocument>();
        if (!Directory.Exists(root))
        {
            return pending;
        }

        foreach (var marker in Directory.EnumerateFiles(root, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(marker), out var operationId))
            {
                _logger.LogError(
                    "Permalink pending marker '{Marker}' has an unparseable name and was left in "
                    + "place unread.",
                    marker);
                continue;
            }

            if (GetActiveSettledPhase(operationId) is not null)
            {
                TryDeleteMarker(marker, operationId, "settled");
                continue;
            }

            var operation = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is not null)
            {
                pending[operationId] = operation;
                continue;
            }

            operation = await ReconstructOperationFromMarkerAsync(marker, operationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation is not null)
            {
                pending[operationId] = operation;
            }
        }

        return pending;
    }

    /// <summary>
    /// Rebuilds <c>operation.json</c> from a marker whose operation was never found, because the
    /// marker's bytes are already the canonical document and republishing them is exact-byte-safe
    /// even if the original writer is still mid-flight and about to publish the same bytes itself.
    /// </summary>
    /// <param name="markerPath">The marker file path.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reconstructed operation, or null when the marker itself cannot be parsed.</returns>
    private async Task<PermalinkOperationDocument?> ReconstructOperationFromMarkerAsync(
        string markerPath,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        byte[] markerBytes;
        PermalinkOperationDocument operation;
        try
        {
            markerBytes = await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false);
            operation = CanonicalJson.Deserialize<PermalinkOperationDocument>(markerBytes, markerPath);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            _logger.LogError(
                exception,
                "Permalink pending marker for operation {OperationId} could not be read and its "
                + "operation could not be reconstructed; it remains fenced until repaired by hand.",
                operationId);
            return null;
        }

        var root = GetActiveOperationPath(operationId);
        _fileSystem.CreateDirectoryDurable(root);
        await PublishExactAsync(Path.Combine(root, "operation.json"), markerBytes, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogWarning(
            "Reconstructed operation.json for {OperationId} from its durable pending marker; "
            + "operation.json was missing, which only a crash between the two publishes explains.",
            operationId);
        return operation;
    }

    private void TryDeleteMarker(string markerPath, Guid operationId, string reason)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Could not remove the {Reason} pending marker for operation {OperationId}; it will "
                + "self-heal the next time the pending set is loaded.",
                reason,
                operationId);
        }
    }

    /// <summary>
    /// Runs the one-time walk of <c>operations/</c> that discovers every pending operation predating
    /// the durable marker area, publishing a marker for each one it finds, then never runs again.
    /// </summary>
    /// <remarks>
    /// Resumable because nothing else can create a new operation directory while this method is
    /// running: it only ever executes from <see cref="PermalinkOperationReconciler.StartAsync"/>,
    /// which the generic host awaits before serving a single request, so <c>operations/</c> cannot
    /// grow underneath a resumed attempt. Directories are visited in ordinal order by name so the
    /// resume cursor (the last name fully verified) is well-defined regardless of the order
    /// <see cref="Directory.EnumerateDirectories(string)"/> happens to return on a given attempt.
    /// </remarks>
    /// <param name="state">The migration state read at the start of this attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pending operations discovered by this walk.</returns>
    private async Task<Dictionary<Guid, PermalinkOperationDocument>> MigratePendingSetAsync(
        PermalinkPendingMigrationState state,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var startingDirectoriesSeen = state.DirectoriesSeen;
        if (startingDirectoriesSeen > 0)
        {
            _logger.LogInformation(
                "Permalink pending-set migration resuming: {Directories} directories already "
                + "verified as of cursor '{Cursor}'.",
                startingDirectoriesSeen,
                state.LastOperationId);
        }

        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        var directoriesSeen = startingDirectoriesSeen;
        var lastVerified = state.LastOperationId;
        if (Directory.Exists(root))
        {
            var names = Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal);
            var sinceCheckpoint = 0;
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lastVerified is not null && string.CompareOrdinal(name, lastVerified) <= 0)
                {
                    continue;
                }

                if (Guid.TryParse(name, out var operationId)
                    && GetActiveSettledPhase(operationId) is null)
                {
                    var candidate = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
                    if (candidate is not null)
                    {
                        var markerPath = GetPendingMarkerPath(operationId);
                        _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(markerPath)!);
                        await PublishExactAsync(
                            markerPath,
                            CanonicalJson.Serialize(candidate),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                lastVerified = name;
                directoriesSeen++;
                sinceCheckpoint++;
                if (sinceCheckpoint >= _pendingMigrationBatchSize)
                {
                    await CheckpointMigrationAsync(lastVerified, directoriesSeen, cancellationToken)
                        .ConfigureAwait(false);
                    sinceCheckpoint = 0;
                    FaultForTestingIfThresholdCrossed(startingDirectoriesSeen, directoriesSeen);
                }
            }

            if (sinceCheckpoint > 0)
            {
                await CheckpointMigrationAsync(lastVerified, directoriesSeen, cancellationToken)
                    .ConfigureAwait(false);
                FaultForTestingIfThresholdCrossed(startingDirectoriesSeen, directoriesSeen);
            }
        }

        await CompleteMigrationAsync(lastVerified, directoriesSeen, cancellationToken).ConfigureAwait(false);

        // A resumed walk only visits directories after its cursor, so an operation whose marker an
        // EARLIER, interrupted attempt already published would be missing from what this call alone
        // just found. The marker area is the ground truth for "pending right now" regardless of how
        // many attempts it took to finish walking; read it back rather than trusting this call's own
        // partial tally.
        var pending = await ComputePendingFromMarkersAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        _lastWalk = new PermalinkJournalWalk((int)Math.Min(directoriesSeen, int.MaxValue), pending.Count, elapsed);
        _logger.LogInformation(
            "Permalink journal walk: {Directories} operation directories, {Pending} still pending, "
            + "{WalkMs:F1} ms, index {IndexState}.",
            directoriesSeen,
            pending.Count,
            elapsed.TotalMilliseconds,
            _pendingIndexEnabled ? "enabled" : "disabled");
        return pending;
    }

    /// <summary>
    /// Throws once per migration if the caller configured a directory count to fault after, so a
    /// restart mid-migration can be exercised deterministically instead of waiting for a real crash.
    /// </summary>
    /// <param name="startingDirectoriesSeen">The durable count when this attempt began.</param>
    /// <param name="directoriesSeen">The durable count just checkpointed.</param>
    private void FaultForTestingIfThresholdCrossed(long startingDirectoriesSeen, long directoriesSeen)
    {
        if (_pendingMigrationFaultAfterDirectories > 0
            && startingDirectoriesSeen < _pendingMigrationFaultAfterDirectories
            && directoriesSeen >= _pendingMigrationFaultAfterDirectories)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "pending-migration-fault-injected",
                $"Pending-set migration fault injected by {PendingMigrationFaultAfterDirectoriesKey} "
                + $"after {directoriesSeen} directories.");
        }
    }

    private async Task<PermalinkPendingMigrationState> ReadMigrationStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO PermalinkPendingMigration
                    (name, last_operation_id, directories_seen, completed_at, created_at)
                VALUES ('v1', NULL, 0, NULL, $created);
                """;
            insert.Parameters.AddWithValue("$created", UtcNow());
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT last_operation_id, directories_seen, completed_at
              FROM PermalinkPendingMigration
             WHERE name = 'v1';
            """;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PermalinkPendingMigrationState(null, 0, false);
        }

        var lastOperationIdIsNull = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false);
        var completedAtIsNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
        return new PermalinkPendingMigrationState(
            lastOperationIdIsNull ? null : reader.GetString(0),
            reader.GetInt64(1),
            !completedAtIsNull);
    }

    private async Task CheckpointMigrationAsync(
        string? lastOperationId,
        long directoriesSeen,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PermalinkPendingMigration
               SET last_operation_id = $last, directories_seen = $seen
             WHERE name = 'v1';
            """;
        command.Parameters.AddWithValue("$last", (object?)lastOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", directoriesSeen);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteMigrationAsync(
        string? lastOperationId,
        long directoriesSeen,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PermalinkPendingMigration
               SET last_operation_id = $last, directories_seen = $seen, completed_at = $completed
             WHERE name = 'v1';
            """;
        command.Parameters.AddWithValue("$last", (object?)lastOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", directoriesSeen);
        command.Parameters.AddWithValue("$completed", UtcNow());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments the durable total-operations-ever-created counter, the one number a boot can still
    /// report honestly for "how large is the journal" without enumerating it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task IncrementDirectoryCounterAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO PermalinkPendingMigration
                    (name, last_operation_id, directories_seen, completed_at, created_at)
                VALUES ('v1', NULL, 0, NULL, $created);
                """;
            insert.Parameters.AddWithValue("$created", UtcNow());
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE PermalinkPendingMigration
               SET directories_seen = directories_seen + 1
             WHERE name = 'v1';
            """;
        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ReadDirectoryCounterAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT directories_seen FROM PermalinkPendingMigration WHERE name = 'v1';";
        var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long seen ? seen : 0;
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private async Task QueueArchiveCandidateAsync(
        Guid operationId,
        PermalinkPhase phase,
        PermalinkOperationPhase document,
        CancellationToken cancellationToken)
    {
        if (!document.OperationId.Equals(operationId)
            || !string.Equals(document.State, phase.Name, StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                document.CreatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var terminalAt))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-terminal-invalid",
                $"Operation '{operationId}' has invalid terminal candidate evidence.");
        }

        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var now = UtcNow();
        var canonicalTerminalAt = terminalAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO PermalinkOperationArchiveCandidates
                    (operation_id, terminal_phase, terminal_at, next_attempt_at,
                     attempts, created_at, updated_at)
                VALUES ($operation, $phase, $terminal, $next, 0, $created, $updated);
                """;
            insert.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$phase", phase.Name);
            insert.Parameters.AddWithValue("$terminal", canonicalTerminalAt);
            insert.Parameters.AddWithValue("$next", now);
            insert.Parameters.AddWithValue("$created", now);
            insert.Parameters.AddWithValue("$updated", now);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT terminal_phase, terminal_at
              FROM PermalinkOperationArchiveCandidates
             WHERE operation_id = $operation;
            """;
        select.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), phase.Name, StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var recordedTerminalAt)
            || recordedTerminalAt != terminalAt)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-archive-candidate-conflict",
                $"Operation '{operationId}' has conflicting terminal archive evidence.");
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Moves indexed old terminal operation directories from the hot journal to the
    /// month-partitioned archive without rewriting any record or listing the flat journal root.
    /// </summary>
    /// <remarks>
    /// This runs only after the pending index has been primed and abandoned work reconciled. Each
    /// move uses the same create-exclusive, fsynced atomic directory publication primitive as
    /// permalink content publication. Therefore an interruption can expose the source or the
    /// destination, never a partially copied replacement. Invalid, linked, pending, suspended, or
    /// destination-conflicting entries are retained in the hot journal for inspection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Counts describing this bounded maintenance pass.</returns>
    public async Task<PermalinkOperationArchiveResult> ArchiveTerminalOperationsAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var operationsRoot = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        if (IsSymbolicLink(operationsRoot))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "operation-journal-linked",
                "The permalink hot operation journal root is a symbolic link and was refused.");
        }

        var examined = 0;
        var archived = 0;
        var retained = 0;
        var refused = 0;
        var failed = 0;
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _operationJournalRetention;
        var candidates = await ReadDueArchiveCandidatesAsync(now, cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            if (!Guid.TryParseExact(candidate.OperationId, "D", out var operationId)
                || !string.Equals(
                    candidate.OperationId,
                    operationId.ToString("D"),
                    StringComparison.Ordinal)
                || candidate.Attempts < 0
                || !DateTimeOffset.TryParse(
                    candidate.TerminalAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var candidateTerminalAt))
            {
                refused++;
                _logger.LogError(
                    "Refusing invalid permalink archive candidate '{OperationId}'.",
                    candidate.OperationId);
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var source = GetActiveOperationPath(operationId);
            var terminal = PermalinkPhase.Declared.FirstOrDefault(
                phase => phase.Disposition == PermalinkPhaseDisposition.Terminal
                    && string.Equals(phase.Name, candidate.TerminalPhase, StringComparison.Ordinal));
            if (terminal is null)
            {
                refused++;
                _logger.LogError(
                    "Refusing permalink archive candidate {OperationId}: terminal phase '{Phase}' "
                    + "is not declared.",
                    operationId,
                    candidate.TerminalPhase);
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var month = candidateTerminalAt.UtcDateTime.ToString(
                "yyyy-MM",
                CultureInfo.InvariantCulture);
            var archiveRoot = GetArchiveRoot();
            var monthRoot = Path.Combine(archiveRoot, month);
            var destination = Path.Combine(monthRoot, operationId.ToString("D"));
            if (IsSymbolicLink(archiveRoot)
                || IsSymbolicLink(monthRoot)
                || IsSymbolicLink(destination))
            {
                refused++;
                _logger.LogError(
                    "Refusing permalink archive candidate {OperationId}: its archive path is linked.",
                    operationId);
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!Directory.Exists(source))
            {
                if (Directory.Exists(destination))
                {
                    try
                    {
                        await ValidateArchiveDirectoryAsync(
                            destination,
                            operationId,
                            terminal,
                            candidateTerminalAt,
                            cancellationToken).ConfigureAwait(false);
                        await DeleteArchiveCandidateAsync(candidate.OperationId, cancellationToken)
                            .ConfigureAwait(false);
                        archived++;
                        continue;
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or PermalinkException)
                    {
                        refused++;
                        _logger.LogError(
                            exception,
                            "Refusing incomplete archive destination for operation {OperationId}; "
                            + "the candidate remains queued.",
                            operationId);
                        await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                failed++;
                _logger.LogError(
                    "Permalink archive candidate {OperationId} is readable at neither its hot nor "
                    + "expected archive path; retaining the candidate for retry.",
                    operationId);
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (IsSymbolicLink(source))
            {
                refused++;
                _logger.LogError(
                    "Refusing to archive permalink operation {OperationId}: the hot journal entry "
                    + "is linked.",
                    operationId);
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var moveAttempted = false;
            try
            {
                var pendingMarker = GetPendingMarkerPath(operationId);
                if (File.Exists(pendingMarker) || IsSymbolicLink(pendingMarker))
                {
                    refused++;
                    _logger.LogInformation(
                        "Retaining permalink operation {OperationId} in the hot journal because it "
                        + "is non-terminal or still has a pending marker.",
                        operationId);
                    await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await ValidateArchiveDirectoryAsync(
                    source,
                    operationId,
                    terminal,
                    candidateTerminalAt,
                    cancellationToken).ConfigureAwait(false);

                if (candidateTerminalAt > cutoff)
                {
                    retained++;
                    await ScheduleArchiveCandidateAsync(
                        candidate.OperationId,
                        candidateTerminalAt + _operationJournalRetention,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new PermalinkException(
                        PermalinkErrorKind.Conflict,
                        "operation-archive-exclusive",
                        $"Archive destination for operation '{operationId}' already exists.");
                }

                moveAttempted = true;
                _fileSystem.PublishDirectoryImmutable(source, destination);
                await DeleteArchiveCandidateAsync(candidate.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                archived++;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or PermalinkException)
            {
                var sourceExists = Directory.Exists(source);
                var destinationExists = Directory.Exists(destination);
                if (moveAttempted && !sourceExists && !destinationExists)
                {
                    throw new PermalinkException(
                        PermalinkErrorKind.Unavailable,
                        "operation-archive-lost",
                        $"Archive failure left operation '{operationId}' unreadable at both locations.",
                        exception);
                }

                if (!sourceExists && destinationExists)
                {
                    await DeleteArchiveCandidateAsync(candidate.OperationId, cancellationToken)
                        .ConfigureAwait(false);
                    archived++;
                    _logger.LogError(
                        exception,
                        "Archiving permalink operation {OperationId} raised after the atomic rename; "
                        + "the complete destination remains readable at '{Destination}'.",
                        operationId,
                        destination);
                    continue;
                }

                failed++;
                await DeferArchiveCandidateAsync(candidate, now, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogError(
                    exception,
                    "Could not archive permalink operation {OperationId}; its hot source remains "
                    + "readable at '{Source}'.",
                    operationId,
                    source);
            }
        }

        return new PermalinkOperationArchiveResult(
            examined,
            archived,
            retained,
            refused,
            failed,
            _operationJournalArchiveMaximumOperationsPerPass);
    }

    private static async Task ValidateArchiveDirectoryAsync(
        string root,
        Guid operationId,
        PermalinkPhase terminal,
        DateTimeOffset candidateTerminalAt,
        CancellationToken cancellationToken)
    {
        var operationPath = Path.Combine(root, "operation.json");
        var operation = CanonicalJson.Deserialize<PermalinkOperationDocument>(
            await File.ReadAllBytesAsync(operationPath, cancellationToken).ConfigureAwait(false),
            operationPath);
        if (!operation.OperationId.Equals(operationId))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-document-id-mismatch",
                $"Operation directory '{operationId}' contains document '{operation.OperationId}'.");
        }

        var terminalPath = Path.Combine(root, terminal.Name + ".json");
        var terminalDocument = CanonicalJson.Deserialize<PermalinkOperationPhase>(
            await File.ReadAllBytesAsync(terminalPath, cancellationToken).ConfigureAwait(false),
            terminalPath);
        if (!terminalDocument.OperationId.Equals(operationId)
            || !string.Equals(terminalDocument.State, terminal.Name, StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                terminalDocument.CreatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var terminalAt))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-terminal-invalid",
                $"Operation '{operationId}' has an invalid terminal phase document.");
        }

        if (terminalAt != candidateTerminalAt)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-archive-candidate-mismatch",
                $"Operation '{operationId}' terminal evidence differs from its archive candidate.");
        }
    }

    private async Task<IReadOnlyList<ArchiveCandidate>> ReadDueArchiveCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = new List<ArchiveCandidate>();
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, terminal_phase, terminal_at, attempts
              FROM PermalinkOperationArchiveCandidates
             WHERE next_attempt_at <= $now
             ORDER BY next_attempt_at, created_at, operation_id
             LIMIT $maximum;
            """;
        command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$maximum", _operationJournalArchiveMaximumOperationsPerPass);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ArchiveCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return result;
    }

    private async Task ScheduleArchiveCandidateAsync(
        string operationId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PermalinkOperationArchiveCandidates
               SET next_attempt_at = $next, attempts = 0, updated_at = $updated
             WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue(
            "$next",
            nextAttemptAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", UtcNow());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeferArchiveCandidateAsync(
        ArchiveCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exponent = (int)Math.Clamp(candidate.Attempts, 0, 9);
        var delay = TimeSpan.FromMinutes(Math.Min(24 * 60, 5 * (1 << exponent)));
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PermalinkOperationArchiveCandidates
               SET next_attempt_at = $next,
                   attempts = CASE WHEN attempts BETWEEN 0 AND 1000000 THEN attempts + 1 ELSE 1 END,
                   updated_at = $updated
             WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", candidate.OperationId);
        command.Parameters.AddWithValue(
            "$next",
            (now + delay).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", UtcNow());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteArchiveCandidateAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM PermalinkOperationArchiveCandidates WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool IsSymbolicLink(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FlushLookupRun();
        _pendingIndexLock.Dispose();
    }

    /// <summary>Publishes bytes create-exclusively, tolerating an exact-byte-identical retry.</summary>
    /// <param name="path">The destination path.</param>
    /// <param name="bytes">The canonical bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call performed the create-exclusive publish;
    /// <see langword="false"/> when the destination already held the exact same bytes. A caller that
    /// counts creations (<see cref="IncrementDirectoryCounterAsync"/>) must use this to avoid
    /// double-counting a retried publish that lands on the byte-compare branch.
    /// </returns>
    private async Task<bool> PublishExactAsync(
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
                return true;
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

        return false;
    }

    private sealed record ArchiveCandidate(
        string OperationId,
        string TerminalPhase,
        string TerminalAt,
        long Attempts);
}
