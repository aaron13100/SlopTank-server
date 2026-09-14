// SlopTank modification notice: added or changed by SlopTank on 2026-09-14.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Copies legacy claim rows into the canonical claim tables in shadow mode.
/// </summary>
/// <remarks>
/// Slice 1 of the P1 quiesced migration in
/// docs/internal/permalink-orphan-resolution-design-20260913.md: the copy is
/// purely additive, no election path reads CanonicalTransitionClaims yet, and
/// MutationClaims rows are counted but deliberately not mapped, because the
/// root-to-event fold needs journal access and lands in a later slice. The
/// caller owns transaction scope so the full migration can later run this
/// copy inside its single authority transaction.
/// </remarks>
internal static class PermalinkCanonicalClaimMigration
{
    /// <summary>
    /// Copies every ContentTransitionClaims row into CanonicalTransitionClaims
    /// under the content-domain canonical key without changing its
    /// predecessor, event id, digest, or owner.
    /// </summary>
    /// <param name="connection">The open SQLite authority connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pass counts and the contested-predecessor log.</returns>
    public static async Task<PermalinkCanonicalClaimMigrationResult> MigrateShadowAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var legacyRows = new List<LegacyContentClaim>();
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.CommandText = """
                SELECT capsule_id, predecessor, operation_id, event_id, event_digest, created_at
                  FROM ContentTransitionClaims
                 ORDER BY capsule_id, predecessor
                """;
            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                legacyRows.Add(new LegacyContentClaim(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        var contested = new List<string>();
        var contentRowsCopied = 0;
        foreach (var row in legacyRows)
        {
            var existing = await ReadCanonicalHolderAsync(
                connection,
                row.CapsuleId,
                row.Predecessor,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                // An identical row is a completed earlier pass (merge); a
                // different owner, decision, event, or digest is a genuine
                // collision and is logged, never overwritten or thrown on.
                if (!string.Equals(existing.OperationId, row.OperationId, StringComparison.Ordinal)
                    || !string.Equals(existing.ProposedDecision, "ready", StringComparison.Ordinal)
                    || !string.Equals(existing.ProposedEventId, row.EventId, StringComparison.Ordinal)
                    || !string.Equals(existing.CanonicalEventDigest, row.EventDigest, StringComparison.Ordinal))
                {
                    contested.Add(DescribeContested(row, existing));
                }

                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO CanonicalTransitionClaims
                    (domain, capsule_id, binding_key, predecessor_event_id,
                     operation_id, proposed_decision, proposed_event_id,
                     canonical_event_digest, created_at)
                VALUES
                    ('content', $capsule, '', $predecessor,
                     $operation, 'ready', $event, $digest, $created)
                """;
            command.Parameters.AddWithValue("$capsule", row.CapsuleId);
            command.Parameters.AddWithValue("$predecessor", row.Predecessor);
            command.Parameters.AddWithValue("$operation", row.OperationId);
            command.Parameters.AddWithValue("$event", row.EventId);
            command.Parameters.AddWithValue("$digest", row.EventDigest);
            command.Parameters.AddWithValue("$created", row.CreatedAt);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                contentRowsCopied++;
            }
        }

        var mutationRowsSeen = 0;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = """
                SELECT COUNT(*) FROM MutationClaims
                """;
            var scalar = await countCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            mutationRowsSeen = scalar is null ? 0 : (int)(long)scalar;
        }

        // Zero until the journal-fold slice maps predecessor roots to event
        // ids through LegacyPredecessorMap.
        const int MutationRowsMapped = 0;
        return new PermalinkCanonicalClaimMigrationResult(
            contentRowsCopied,
            mutationRowsSeen,
            MutationRowsMapped,
            contested.Count,
            contested);
    }

    private static async Task<CanonicalClaimHolder?> ReadCanonicalHolderAsync(
        SqliteConnection connection,
        string capsuleId,
        string predecessor,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, proposed_decision, proposed_event_id, canonical_event_digest
              FROM CanonicalTransitionClaims
             WHERE domain = 'content'
               AND capsule_id = $capsule
               AND binding_key = ''
               AND predecessor_event_id = $predecessor
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId);
        command.Parameters.AddWithValue("$predecessor", predecessor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CanonicalClaimHolder(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3))
            : null;
    }

    private static string DescribeContested(LegacyContentClaim row, CanonicalClaimHolder existing)
    {
        return $"content capsule '{row.CapsuleId}' predecessor '{row.Predecessor}': "
            + $"canonical row held by operation '{existing.OperationId}' with decision "
            + $"'{existing.ProposedDecision}', event '{existing.ProposedEventId}', digest "
            + $"'{existing.CanonicalEventDigest}'; legacy row names operation "
            + $"'{row.OperationId}', event '{row.EventId}', digest '{row.EventDigest}'.";
    }

    /// <summary>Summarizes one shadow canonical-claim migration pass.</summary>
    /// <param name="ContentRowsCopied">Legacy content rows newly copied this pass.</param>
    /// <param name="MutationRowsSeen">Legacy mutation rows examined this pass.</param>
    /// <param name="MutationRowsMapped">Mutation rows folded to event ids; always zero until the journal-fold slice lands.</param>
    /// <param name="Contested">Rows whose copy would violate the canonical primary key.</param>
    /// <param name="ContestedPredecessors">One descriptive line per contested row.</param>
    internal sealed record PermalinkCanonicalClaimMigrationResult(
        int ContentRowsCopied,
        int MutationRowsSeen,
        int MutationRowsMapped,
        int Contested,
        IReadOnlyList<string> ContestedPredecessors);

    private sealed record LegacyContentClaim(
        string CapsuleId,
        string Predecessor,
        string OperationId,
        string EventId,
        string EventDigest,
        string CreatedAt);

    private sealed record CanonicalClaimHolder(
        string OperationId,
        string ProposedDecision,
        string ProposedEventId,
        string CanonicalEventDigest);
}
