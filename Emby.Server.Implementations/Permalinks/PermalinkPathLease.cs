// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-02.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The lease implementation: owns the keyed lock handle, the anchor-intent row of an anchor it
/// created, and the per-key rechecks that run inside Ensure's authority transaction (P6
/// decisions, "Recheck inside Ensure's transaction").
/// </summary>
internal sealed class PermalinkPathLease : IPermalinkPathLease
{
    /// <summary>The pinned-identity value of a virtual season directory, which has no stable inode.</summary>
    public const string VirtualPin = "virtual";

    private readonly IDisposable _keyLock;
    private readonly string _path;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly IPermalinkPathObjects _objects;
    private readonly PermalinkPathEvidenceStore _evidence;
    private readonly PermalinkAnchorIntentSettlement _settlement;
    private readonly Guid? _intentId;
    private int _disposed;

    public PermalinkPathLease(
        IDisposable keyLock,
        string path,
        PermalinkRootIdentity root,
        string normalizedRelativePath,
        string pinnedObjectIdentity,
        string anchorToken,
        Guid? intentId,
        long reservationGeneration,
        long contentEpochAtAcquire,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects,
        PermalinkPathEvidenceStore evidence)
    {
        _keyLock = keyLock;
        _path = path;
        RootIdentityId = root.RootIdentityId;
        NormalizedRelativePath = normalizedRelativePath;
        PinnedObjectIdentity = pinnedObjectIdentity;
        AnchorToken = anchorToken;
        _intentId = intentId;
        ReservationGeneration = reservationGeneration;
        ContentEpochAtAcquire = contentEpochAtAcquire;
        _fileSystem = fileSystem;
        _objects = objects;
        _evidence = evidence;
        _settlement = new PermalinkAnchorIntentSettlement(evidence, fileSystem, objects);
    }

    /// <inheritdoc />
    public string RootIdentityId { get; }

    /// <inheritdoc />
    public string NormalizedRelativePath { get; }

    /// <inheritdoc />
    public string PinnedObjectIdentity { get; }

    /// <inheritdoc />
    public string AnchorToken { get; }

    /// <inheritdoc />
    public bool CreatedAnchor => _intentId is not null;

    /// <inheritdoc />
    public long ReservationGeneration { get; }

    /// <inheritdoc />
    public long ContentEpochAtAcquire { get; }

    /// <summary>
    /// Rechecks every per-key invariant and marks this lease's anchor intent committed. It runs
    /// inside the authority transaction that elects or adopts the capsule, so any refusal rolls
    /// that election back with it. A change in the global content epoch is deliberately not a
    /// refusal: every write anywhere bumps it, so refusing would fail Ensure during any scan.
    /// </summary>
    /// <param name="connection">The authority connection.</param>
    /// <param name="transaction">Ensure's transaction.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    /// <exception cref="PermalinkException">Conflict with a <c>permalink-*</c> code when the key changed.</exception>
    public async Task RecheckAndCommitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        RecheckObject();
        var root = RootIdentityId;
        var path = NormalizedRelativePath;
        var activeReservations = await _evidence.CountActiveReservationsAsync(
            connection, transaction, root, path, cancellationToken).ConfigureAwait(false);
        var maxGeneration = await _evidence.ReadMaxReservationGenerationAsync(
            connection, transaction, root, path, cancellationToken).ConfigureAwait(false);
        if (activeReservations > 0 || maxGeneration != ReservationGeneration)
        {
            throw Conflict(
                "permalink-path-reserved",
                $"Path '{path}' was reserved while its identity was being established (generation {ReservationGeneration} became {maxGeneration}).");
        }

        if (await _evidence.CountPendingSlotsAsync(connection, transaction, root, path, cancellationToken)
            .ConfigureAwait(false) > 0)
        {
            throw Conflict(
                "permalink-supersession-pending",
                $"Path '{path}' has a pending supersession slot and cannot mint a genesis.");
        }

        if (await _evidence.CountBlockingAdmissionsAsync(connection, transaction, root, path, cancellationToken)
            .ConfigureAwait(false) > 0)
        {
            throw Conflict(
                "permalink-path-writer-admitted",
                $"Path '{path}' has an admitted writer that has not completed.");
        }

        if (_intentId is { } intent)
        {
            await _evidence.SetIntentStateAsync(
                connection,
                transaction,
                intent,
                PermalinkPathEvidenceStore.IntentCommitted,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the key. A lease that created the anchor and never reached a committed intent
    /// removes that anchor again (compare-and-remove) and marks the intent abandoned, so an
    /// anchor with no committed registry record cannot outlive the lease.
    /// </summary>
    /// <returns>A task representing the release.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_intentId is { } intent)
            {
                var state = await _evidence.ReadIntentStateAsync(intent, CancellationToken.None).ConfigureAwait(false);
                if (string.Equals(state, PermalinkPathEvidenceStore.IntentPending, StringComparison.Ordinal))
                {
                    _ = await _settlement.AbandonAsync(intent, _path, AnchorToken, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _keyLock.Dispose();
        }
    }

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private void RecheckObject()
    {
        var virtualPath = string.Equals(PinnedObjectIdentity, VirtualPin, StringComparison.Ordinal);
        var pin = virtualPath
            ? (System.IO.Directory.Exists(_path) ? VirtualPin : null)
            : _objects.ReadPinnedIdentityNoFollow(_path);
        var anchor = pin is null ? null : _fileSystem.ReadAnchorToken(_path);
        if (!string.Equals(pin, PinnedObjectIdentity, StringComparison.Ordinal)
            || !string.Equals(anchor, AnchorToken, StringComparison.Ordinal))
        {
            throw Conflict(
                "permalink-path-object-changed",
                $"The object at '{NormalizedRelativePath}' changed while its identity was being established.");
        }
    }
}
