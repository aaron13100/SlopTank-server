// SlopTank modification notice: added or changed by SlopTank on 2026-09-14, 2026-10-10.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Copies legacy claim rows into the canonical claim tables in shadow mode.
/// </summary>
/// <remarks>
/// Slices 1 and 2 of the P1 quiesced migration in
/// docs/internal/permalink-orphan-resolution-design-20260913.md: the copy is
/// purely additive, no election path reads CanonicalTransitionClaims yet, and
/// MutationClaims rows are folded root-to-event through their immutable
/// operation journal and the capsule store, quarantining every row that does
/// not fold to exactly one event instead of electing by row order. The caller
/// owns transaction scope so the full migration can later run this copy inside
/// its single authority transaction.
/// </remarks>
internal static class PermalinkCanonicalClaimMigration
{
    private enum LegacyFoldOutcome
    {
        Mapped,
        Contested,
        PathClaimDeferred
    }

    /// <summary>
    /// Copies every ContentTransitionClaims row into CanonicalTransitionClaims
    /// under the content-domain canonical key without changing its
    /// predecessor, event id, digest, or owner, then folds every
    /// MutationClaims row root to exactly one content event.
    /// </summary>
    /// <param name="connection">The open SQLite authority connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="journalOperationReader">
    /// Reads one operation document by id. When null, the journal living
    /// beside the authority database is read directly, hot directory first
    /// and then the month-partitioned archive; injectable so tests never
    /// touch the real journal root.
    /// </param>
    /// <param name="capsuleEventReader">
    /// Reads the events of one capsule store directory. When null, the
    /// directory is read directly; injectable so tests never touch the real
    /// capsule roots.
    /// </param>
    /// <returns>The pass counts and the contested-predecessor log.</returns>
    public static async Task<PermalinkCanonicalClaimMigrationResult> MigrateShadowAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        Func<Guid, CancellationToken, Task<PermalinkOperationDocument?>>? journalOperationReader = null,
        Func<string, CancellationToken, Task<IReadOnlyList<PermalinkLegacyEventEvidence>>>? capsuleEventReader = null)
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

        var mutationRows = new List<LegacyMutationClaim>();
        await using (var mutationReadCommand = connection.CreateCommand())
        {
            mutationReadCommand.CommandText = """
                SELECT capsule_id, predecessor_root, operation_id, created_at
                  FROM MutationClaims
                 ORDER BY capsule_id, predecessor_root
                """;
            await using var reader = await mutationReadCommand.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                mutationRows.Add(new LegacyMutationClaim(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var readOperationAsync = journalOperationReader
            ?? ((operationId, token) => ReadJournalOperationAsync(connection, operationId, token));
        var readCapsuleEventsAsync = capsuleEventReader
            ?? ((capsulePath, token) => ReadCapsuleEventsAsync(capsulePath, token));
        var mutationRowsMapped = 0;
        foreach (var row in mutationRows)
        {
            var fold = await FoldMutationClaimAsync(
                connection,
                readOperationAsync,
                readCapsuleEventsAsync,
                row,
                cancellationToken).ConfigureAwait(false);
            if (fold.Outcome == LegacyFoldOutcome.Mapped)
            {
                mutationRowsMapped++;
            }
            else if (fold.Outcome == LegacyFoldOutcome.Contested)
            {
                contested.Add(fold.Reason);
            }

            // PathClaimDeferred: design migration step 3 recovers binding id
            // and path predecessor event from the bundle in a later slice.
        }

        return new PermalinkCanonicalClaimMigrationResult(
            contentRowsCopied,
            mutationRows.Count,
            mutationRowsMapped,
            contested.Count,
            contested);
    }

    /// <summary>
    /// Folds one legacy MutationClaims row to a content event under the
    /// design's migration steps 2, 4, and 5.
    /// </summary>
    /// <param name="connection">The open SQLite authority connection.</param>
    /// <param name="readOperationAsync">The journal operation reader.</param>
    /// <param name="readCapsuleEventsAsync">The capsule event reader.</param>
    /// <param name="row">The legacy mutation claim row.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fold outcome and, when contested, its reason line.</returns>
    private static async Task<LegacyFoldResult> FoldMutationClaimAsync(
        SqliteConnection connection,
        Func<Guid, CancellationToken, Task<PermalinkOperationDocument?>> readOperationAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<PermalinkLegacyEventEvidence>>> readCapsuleEventsAsync,
        LegacyMutationClaim row,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(row.OperationId, "D", out var operationId))
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': "
                + $"operation id '{row.OperationId}' is not a durable operation id.");
        }

        PermalinkOperationDocument? operation;
        try
        {
            operation = await readOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or PermalinkException)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the journal "
                + $"bundle of operation '{row.OperationId}' could not be read ({exception.Message}).");
        }

        if (operation is null)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': operation "
                + $"'{row.OperationId}' has no readable journal bundle.");
        }

        if (!Guid.TryParseExact(row.CapsuleId, "D", out var capsuleId))
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the capsule "
                + "id is not a durable capsule id.");
        }

        var bundle = PermalinkMediaMutation.RequireBundle(operation);
        var claim = bundle.Claims.FirstOrDefault(candidate => candidate.CapsuleId.Equals(capsuleId));
        if (claim is null)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the journal "
                + $"bundle of operation '{row.OperationId}' claims no predecessor for the capsule.");
        }

        if (string.Equals(claim.Kind, "path", StringComparison.Ordinal))
        {
            return new LegacyFoldResult(LegacyFoldOutcome.PathClaimDeferred, string.Empty);
        }

        if (!string.Equals(claim.Predecessor, row.PredecessorRoot, StringComparison.Ordinal))
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the journal "
                + $"bundle claim predecessor '{claim.Predecessor}' disagrees with the recorded root.");
        }

        var capsulePaths = await ReadCapsulePathsAsync(connection, capsuleId, cancellationToken)
            .ConfigureAwait(false);
        if (capsulePaths.Count == 0)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the authority "
                + "records no capsule path for the capsule.");
        }

        if (capsulePaths.Count > 1)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the authority "
                + $"records {capsulePaths.Count} distinct capsule paths for the capsule.");
        }

        IReadOnlyList<PermalinkLegacyEventEvidence> events;
        try
        {
            events = await readCapsuleEventsAsync(capsulePaths[0], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or PermalinkException)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the capsule at "
                + $"'{capsulePaths[0]}' could not be read ({exception.Message}).");
        }

        // The same content-head rules PermalinkCapsuleStore.ReadValidatedAsync
        // applies: only mint, identity_seed, append-only-discovery,
        // controlled-mutation, and operator-confirmed-adoption events form the
        // content lineage, the head is the
        // content event no other content event names as its predecessor, and
        // a capsule that does not fold to exactly one head is ambiguous.
        var eventIds = new HashSet<Guid>();
        var contentEvents = new List<PermalinkLegacyEventEvidence>();
        foreach (var evidence in events)
        {
            if (!string.Equals(evidence.Event.Type, "sloptank.permalink-event", StringComparison.Ordinal)
                || evidence.Event.Version != 1)
            {
                return Contested(
                    $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the capsule "
                    + $"at '{capsulePaths[0]}' holds a foreign or unknown-version event.");
            }

            if (!eventIds.Add(evidence.Event.EventId))
            {
                return Contested(
                    $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the capsule "
                    + $"at '{capsulePaths[0]}' holds duplicate event id '{evidence.Event.EventId:D}'.");
            }

            if (evidence.Event.Kind is "mint" or "identity_seed"
                or "append-only-discovery" or "controlled-mutation"
                or "operator-confirmed-adoption")
            {
                contentEvents.Add(evidence);
            }
        }

        var heads = contentEvents
            .Where(candidate => !contentEvents.Any(
                other => Nullable.Equals(other.Event.PreviousContentEventId, candidate.Event.EventId)))
            .ToArray();
        if (heads.Length != 1)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the capsule at "
                + $"'{capsulePaths[0]}' folds to {heads.Length} content heads.");
        }

        var matches = contentEvents
            .Where(candidate => string.Equals(
                candidate.Event.ContentRoot,
                row.PredecessorRoot,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': no folded "
                + $"content event records the root (the folded head records "
                + $"'{heads[0].Event.ContentRoot}').");
        }

        if (matches.Length > 1)
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': {matches.Length} "
                + "folded content events record the root.");
        }

        var mapped = matches[0];
        var mappedEventId = mapped.Event.EventId.ToString("D");
        var existing = await ReadCanonicalHolderAsync(
            connection,
            row.CapsuleId,
            mappedEventId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null
            && (!string.Equals(existing.OperationId, row.OperationId, StringComparison.Ordinal)
                || !string.Equals(existing.ProposedDecision, "ready", StringComparison.Ordinal)
                || !string.Equals(existing.ProposedEventId, mappedEventId, StringComparison.Ordinal)
                || !string.Equals(existing.CanonicalEventDigest, mapped.StoredDigest, StringComparison.Ordinal)))
        {
            return Contested(DescribeContestedMutation(row, mapped, existing));
        }

        var existingMap = await ReadLegacyMapHolderAsync(
            connection,
            row.CapsuleId,
            row.PredecessorRoot,
            cancellationToken).ConfigureAwait(false);
        if (existingMap is not null
            && (!string.Equals(existingMap.MappedEventId, mappedEventId, StringComparison.Ordinal)
                || !string.Equals(existingMap.OperationId, row.OperationId, StringComparison.Ordinal)))
        {
            return Contested(
                $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': an earlier pass "
                + $"mapped the root to event '{existingMap.MappedEventId}' for operation "
                + $"'{existingMap.OperationId}', but this pass folds it to event '{mappedEventId}'.");
        }

        await using (var canonicalCommand = connection.CreateCommand())
        {
            canonicalCommand.CommandText = """
                INSERT OR IGNORE INTO CanonicalTransitionClaims
                    (domain, capsule_id, binding_key, predecessor_event_id,
                     operation_id, proposed_decision, proposed_event_id,
                     canonical_event_digest, created_at)
                VALUES
                    ('content', $capsule, '', $predecessor,
                     $operation, 'ready', $event, $digest, $created)
                """;
            canonicalCommand.Parameters.AddWithValue("$capsule", row.CapsuleId);
            canonicalCommand.Parameters.AddWithValue("$predecessor", mappedEventId);
            canonicalCommand.Parameters.AddWithValue("$operation", row.OperationId);
            canonicalCommand.Parameters.AddWithValue("$event", mappedEventId);
            canonicalCommand.Parameters.AddWithValue("$digest", mapped.StoredDigest);
            canonicalCommand.Parameters.AddWithValue("$created", row.CreatedAt);
            _ = await canonicalCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var mapCommand = connection.CreateCommand())
        {
            mapCommand.CommandText = """
                INSERT OR IGNORE INTO LegacyPredecessorMap
                    (capsule_id, predecessor_root, mapped_event_id, operation_id, created_at)
                VALUES ($capsule, $root, $event, $operation, $created)
                """;
            mapCommand.Parameters.AddWithValue("$capsule", row.CapsuleId);
            mapCommand.Parameters.AddWithValue("$root", row.PredecessorRoot);
            mapCommand.Parameters.AddWithValue("$event", mappedEventId);
            mapCommand.Parameters.AddWithValue("$operation", row.OperationId);
            mapCommand.Parameters.AddWithValue("$created", row.CreatedAt);
            _ = await mapCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new LegacyFoldResult(LegacyFoldOutcome.Mapped, string.Empty);
    }

    /// <summary>
    /// Reads one operation document from the journal that lives beside the authority database,
    /// hot directory first and then the month-partitioned archive, refusing an ambiguous archive
    /// by returning null exactly like a missing bundle.
    /// </summary>
    /// <param name="connection">The authority connection whose journal to read.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation document, or null when no exact bundle exists.</returns>
    private static async Task<PermalinkOperationDocument?> ReadJournalOperationAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operationsRoot = GetOperationsRoot(connection);
        if (operationsRoot is null)
        {
            return null;
        }

        var found = new List<string>();
        var hot = Path.Combine(operationsRoot, operationId.ToString("D"));
        if (File.Exists(Path.Combine(hot, "operation.json")))
        {
            found.Add(hot);
        }

        var archiveRoot = Path.Combine(Path.GetDirectoryName(operationsRoot)!, "operations-archive");
        if (Directory.Exists(archiveRoot))
        {
            foreach (var month in Directory.EnumerateDirectories(archiveRoot))
            {
                var candidate = Path.Combine(month, operationId.ToString("D"));
                if (File.Exists(Path.Combine(candidate, "operation.json")))
                {
                    found.Add(candidate);
                }
            }
        }

        if (found.Count != 1)
        {
            return null;
        }

        var path = Path.Combine(found[0], "operation.json");
        return CanonicalJson.Deserialize<PermalinkOperationDocument>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
    }

    /// <summary>
    /// Resolves the hot operations directory with the same convention the journal itself uses:
    /// the authority database lives at {AuthorityRoot}/.sloptank/permalinks/authority.db and
    /// operations/ is its sibling (PermalinkAuthorityStore.AuthorityRoot and
    /// PermalinkOperationJournal.GetActiveOperationPath).
    /// </summary>
    /// <param name="connection">The authority connection.</param>
    /// <returns>The operations directory, or null when the connection has no file-backed root.</returns>
    private static string? GetOperationsRoot(SqliteConnection connection)
    {
        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.Ordinal))
        {
            return null;
        }

        var permalinksRoot = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return permalinksRoot is null ? null : Path.Combine(permalinksRoot, "operations");
    }

    /// <summary>
    /// Reads every event document in one capsule store directory together with the digest of
    /// its exact stored bytes.
    /// </summary>
    /// <param name="capsulePath">The capsule store directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The capsule events in directory order.</returns>
    private static async Task<IReadOnlyList<PermalinkLegacyEventEvidence>> ReadCapsuleEventsAsync(
        string capsulePath,
        CancellationToken cancellationToken)
    {
        var eventsRoot = Path.Combine(capsulePath, "events");
        if (!Directory.Exists(eventsRoot))
        {
            return [];
        }

        var events = new List<PermalinkLegacyEventEvidence>();
        foreach (var path in Directory.GetFiles(eventsRoot, "*.json"))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            events.Add(new PermalinkLegacyEventEvidence(
                CanonicalJson.Deserialize<PermalinkEventDocument>(bytes, path),
                CanonicalJson.Digest(bytes)));
        }

        return events;
    }

    private static async Task<List<string>> ReadCapsulePathsAsync(
        SqliteConnection connection,
        Guid capsuleId,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT capsule_path FROM HistoricalCapsules WHERE capsule_id = $capsule
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
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

    private static async Task<LegacyPredecessorMapHolder?> ReadLegacyMapHolderAsync(
        SqliteConnection connection,
        string capsuleId,
        string predecessorRoot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mapped_event_id, operation_id
              FROM LegacyPredecessorMap
             WHERE capsule_id = $capsule
               AND predecessor_root = $root
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId);
        command.Parameters.AddWithValue("$root", predecessorRoot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new LegacyPredecessorMapHolder(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static LegacyFoldResult Contested(string reason)
    {
        return new LegacyFoldResult(LegacyFoldOutcome.Contested, reason);
    }

    private static string DescribeContested(LegacyContentClaim row, CanonicalClaimHolder existing)
    {
        return $"content capsule '{row.CapsuleId}' predecessor '{row.Predecessor}': "
            + $"canonical row held by operation '{existing.OperationId}' with decision "
            + $"'{existing.ProposedDecision}', event '{existing.ProposedEventId}', digest "
            + $"'{existing.CanonicalEventDigest}'; legacy row names operation "
            + $"'{row.OperationId}', event '{row.EventId}', digest '{row.EventDigest}'.";
    }

    private static string DescribeContestedMutation(
        LegacyMutationClaim row,
        PermalinkLegacyEventEvidence mapped,
        CanonicalClaimHolder existing)
    {
        var sameOwner = string.Equals(existing.OperationId, row.OperationId, StringComparison.Ordinal);
        return $"mutation capsule '{row.CapsuleId}' root '{row.PredecessorRoot}': the root folds to "
            + $"event '{mapped.Event.EventId:D}' with digest '{mapped.StoredDigest}', but the "
            + $"canonical row for that event is held by operation '{existing.OperationId}' with "
            + $"decision '{existing.ProposedDecision}', event '{existing.ProposedEventId}', digest "
            + $"'{existing.CanonicalEventDigest}' "
            + (sameOwner ? "(same-owner evidence disagreement)" : "(different-owner collision)")
            + ".";
    }

    /// <summary>Summarizes one shadow canonical-claim migration pass.</summary>
    /// <param name="ContentRowsCopied">Legacy content rows newly copied this pass.</param>
    /// <param name="MutationRowsSeen">Legacy mutation rows examined this pass.</param>
    /// <param name="MutationRowsMapped">Mutation rows that folded cleanly to exactly one content
    /// event this pass, including rows an earlier pass already recorded identically.</param>
    /// <param name="Contested">Rows whose copy or fold would violate the canonical primary key or the one-event mapping rule.</param>
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

    private sealed record LegacyMutationClaim(
        string CapsuleId,
        string PredecessorRoot,
        string OperationId,
        string CreatedAt);

    private sealed record LegacyPredecessorMapHolder(
        string MappedEventId,
        string OperationId);

    private sealed record LegacyFoldResult(LegacyFoldOutcome Outcome, string Reason);
}
