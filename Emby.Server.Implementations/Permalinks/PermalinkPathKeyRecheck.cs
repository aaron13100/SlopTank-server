// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The per-key invariants every lease holder rechecks under its lease: no active or newly
/// generated reservation, no pending supersession slot, no blocking writer admission. Ensure
/// runs it inside its authority transaction; physical writers run it immediately before each
/// publication step. One implementation, so the two can never drift apart.
/// </summary>
internal static class PermalinkPathKeyRecheck
{
    /// <summary>
    /// Throws a typed Conflict when anything changed on the key since the lease was taken.
    /// </summary>
    /// <param name="evidence">The path evidence store.</param>
    /// <param name="connection">The authority connection.</param>
    /// <param name="transaction">The transaction the reads run in.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="path">The normalized relative path.</param>
    /// <param name="expectedGeneration">The reservation generation recorded when the lease was taken.</param>
    /// <param name="permit">The current external writer's exact capability, or null for an ordinary writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    /// <exception cref="PermalinkException">Conflict <c>permalink-path-reserved</c>, <c>permalink-supersession-pending</c> or <c>permalink-path-writer-admitted</c>.</exception>
    public static async Task RequireClearAsync(
        PermalinkPathEvidenceStore evidence,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string path,
        long expectedGeneration,
        PermalinkWriterAdmissionPermit? permit,
        CancellationToken cancellationToken)
        => await RequireClearCoreAsync(
            evidence,
            connection,
            transaction,
            rootIdentityId,
            path,
            expectedGeneration,
            rejectPendingSupersession: true,
            permit,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies the scanner subset of the key predicate. A pending supersession slot is
    /// intentionally allowed and left untouched: scan discovery may index the item, but only a
    /// later explicit Ensure/share is allowed to consume the slot.
    /// </summary>
    /// <param name="evidence">The path evidence store.</param>
    /// <param name="connection">The authority connection.</param>
    /// <param name="transaction">The transaction the reads run in.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="path">The normalized relative path.</param>
    /// <param name="expectedGeneration">The reservation generation recorded when the lease was taken.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    public static async Task RequireScannerClearAsync(
        PermalinkPathEvidenceStore evidence,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string path,
        long expectedGeneration,
        CancellationToken cancellationToken)
        => await RequireClearCoreAsync(
            evidence,
            connection,
            transaction,
            rootIdentityId,
            path,
            expectedGeneration,
            rejectPendingSupersession: false,
            permit: null,
            cancellationToken).ConfigureAwait(false);

    private static async Task RequireClearCoreAsync(
        PermalinkPathEvidenceStore evidence,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string path,
        long expectedGeneration,
        bool rejectPendingSupersession,
        PermalinkWriterAdmissionPermit? permit,
        CancellationToken cancellationToken)
    {
        var activeReservations = await evidence.CountActiveReservationsAsync(
            connection, transaction, rootIdentityId, path, cancellationToken).ConfigureAwait(false);
        var maxGeneration = await evidence.ReadMaxReservationGenerationAsync(
            connection, transaction, rootIdentityId, path, cancellationToken).ConfigureAwait(false);
        if (activeReservations > 0 || maxGeneration != expectedGeneration)
        {
            throw Conflict(
                "permalink-path-reserved",
                $"Path '{path}' was reserved after its lease was taken (generation {expectedGeneration} became {maxGeneration}).");
        }

        if (rejectPendingSupersession
            && await evidence.CountPendingSlotsAsync(connection, transaction, rootIdentityId, path, cancellationToken)
            .ConfigureAwait(false) > 0)
        {
            throw Conflict(
                "permalink-supersession-pending",
                $"Path '{path}' has a pending supersession slot.");
        }

        if (permit is not null)
        {
            var admitted = await evidence.ReadWriterAdmissionAsync(
                connection,
                transaction,
                rootIdentityId,
                path,
                permit.AdmissionId,
                cancellationToken).ConfigureAwait(false);
            if (admitted is null
                || admitted.Status != "started"
                || admitted.AuthorityEpoch != PermalinkAuthoritySchema.SupportedProtocolEpoch
                || admitted.HeartbeatDeadlineUtc is null
                || admitted.HeartbeatDeadlineUtc < DateTimeOffset.UtcNow
                || !string.Equals(admitted.Action, permit.Action, StringComparison.Ordinal)
                || !string.Equals(admitted.PinnedObjectIdentity, permit.PinnedObjectIdentity, StringComparison.Ordinal))
            {
                throw Conflict(
                    "permalink-writer-admission-invalid",
                    $"Writer admission '{permit.AdmissionId:D}' is not a live exact capability for path '{path}'.");
            }
        }

        if (await evidence.CountBlockingAdmissionsAsync(
                connection,
                transaction,
                rootIdentityId,
                path,
                permit?.AdmissionId,
                cancellationToken)
            .ConfigureAwait(false) > 0)
        {
            throw Conflict(
                "permalink-path-writer-admitted",
                $"Path '{path}' has an admitted writer that has not completed.");
        }
    }

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);
}
