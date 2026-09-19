// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-03, 2026-09-05, 2026-09-08, 2026-09-09, 2026-09-14, 2026-09-19.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns the authority SQLite schema and atomic genesis claim set.
/// </summary>
internal static class PermalinkAuthoritySchema
{
    /// <summary>The newest authority protocol this binary can safely open.</summary>
    internal const long SupportedProtocolEpoch = 1;

    /// <summary>
    /// Refuses an authority created by a newer binary before this binary can
    /// expose HTTP, scans, or scheduled work against state it cannot interpret.
    /// An authority predating the protocol row is epoch zero and remains
    /// readable by the compatibility release.
    /// </summary>
    /// <param name="connection">The open authority connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous validation.</returns>
    public static async Task ValidateProtocolEpochAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var table = connection.CreateCommand();
        table.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'AuthorityProtocolState'";
        if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        await using var epoch = connection.CreateCommand();
        epoch.CommandText = "SELECT protocol_epoch FROM AuthorityProtocolState WHERE singleton = 1";
        var value = await epoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Corrupt,
                "authority-protocol-missing",
                "The permalink authority protocol row is missing.");
        }

        var observed = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        if (observed > SupportedProtocolEpoch)
        {
            throw new PermalinkException(
                PermalinkErrorKind.VersionRefused,
                "authority-protocol-newer",
                $"The permalink authority protocol epoch {observed} is newer than supported epoch {SupportedProtocolEpoch}.");
        }
    }

    /// <summary>
    /// Creates the append-only authority and derived binding tables.
    /// </summary>
    /// <param name="connection">The SQLite authority connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task ApplyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AuthorityProtocolState (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                protocol_epoch INTEGER NOT NULL CHECK(protocol_epoch >= 0),
                irreversible_floor INTEGER NOT NULL DEFAULT 0 CHECK(irreversible_floor IN (0, 1)),
                updated_at TEXT NOT NULL);
            INSERT OR IGNORE INTO AuthorityProtocolState
                (singleton, protocol_epoch, irreversible_floor, updated_at)
            VALUES (1, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            CREATE TABLE IF NOT EXISTS SnapshotMetadata (
                generation INTEGER PRIMARY KEY AUTOINCREMENT,
                authority_id TEXT NOT NULL,
                schema_epoch INTEGER NOT NULL,
                content_epoch INTEGER NOT NULL,
                journal_counter INTEGER NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AuthorityWriteProtection (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                enforcement_enabled INTEGER NOT NULL DEFAULT 0
                    CHECK(enforcement_enabled IN (0, 1)));
            INSERT OR IGNORE INTO AuthorityWriteProtection VALUES (1, 0);
            CREATE TABLE IF NOT EXISTS AuthorityCounters (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                authority_sequence INTEGER NOT NULL CHECK(authority_sequence >= 0),
                content_epoch INTEGER NOT NULL CHECK(content_epoch >= 0),
                journal_counter INTEGER NOT NULL CHECK(journal_counter >= 0));
            INSERT OR IGNORE INTO AuthorityCounters VALUES (1, 0, 0, 0);
            CREATE TRIGGER IF NOT EXISTS protect_authority_counters_update
            BEFORE UPDATE ON AuthorityCounters BEGIN
                SELECT CASE WHEN authority_counter_write() != 1
                    THEN RAISE(ABORT, 'permalink-counter-write') END;
            END;
            CREATE TABLE IF NOT EXISTS AuthorityWriteAuthorizations (
                nonce TEXT PRIMARY KEY, operation_id TEXT NOT NULL,
                table_name TEXT NOT NULL, verb TEXT NOT NULL,
                primary_key TEXT NOT NULL, epoch INTEGER NOT NULL,
                mac TEXT NOT NULL, created_at TEXT NOT NULL,
                UNIQUE(table_name, verb, primary_key));
            CREATE TABLE IF NOT EXISTS AnchorTokenCapsules (
                anchor_token TEXT PRIMARY KEY, capsule_id TEXT NOT NULL,
                binding_id TEXT NOT NULL, root_path TEXT NOT NULL,
                capsule_path TEXT NOT NULL, item_kind TEXT NOT NULL,
                content_root TEXT NOT NULL, current_path TEXT NOT NULL,
                header_json TEXT NOT NULL, event_json TEXT NOT NULL,
                anchor_json TEXT NOT NULL, issued_id TEXT NULL,
                issuance_nonce TEXT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PermalinkIssuances (
                PermalinkId TEXT PRIMARY KEY, IssuanceNonce TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS FirstAliasClaims (
                capsule_id TEXT PRIMARY KEY, permalink_id TEXT NULL,
                issuance_nonce TEXT NULL, alias_event_id TEXT NOT NULL,
                event_digest TEXT NOT NULL, event_json TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PermalinkBindingCapsuleOverrides (
                PermalinkId TEXT NOT NULL, ItemId TEXT NOT NULL,
                capsule_id TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(PermalinkId, ItemId));
            CREATE TABLE IF NOT EXISTS ContentTransitionClaims (
                capsule_id TEXT NOT NULL, predecessor TEXT NOT NULL,
                operation_id TEXT NOT NULL, event_id TEXT NOT NULL,
                event_digest TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(capsule_id, predecessor));
            CREATE TABLE IF NOT EXISTS OperationDecisions (
                operation_id TEXT NOT NULL, capsule_id TEXT NOT NULL,
                predecessor TEXT NOT NULL, decision TEXT NOT NULL,
                event_id TEXT NOT NULL, event_digest TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(operation_id, capsule_id, predecessor));
            CREATE TABLE IF NOT EXISTS PathOperationDecisions (
                operation_id TEXT NOT NULL, capsule_id TEXT NOT NULL,
                binding_id TEXT NOT NULL, predecessor TEXT NOT NULL,
                decision TEXT NOT NULL, event_id TEXT NOT NULL,
                event_digest TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(operation_id, capsule_id, binding_id, predecessor));
            CREATE TABLE IF NOT EXISTS MutationClaims (
                capsule_id TEXT NOT NULL, predecessor_root TEXT NOT NULL,
                operation_id TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(capsule_id, predecessor_root));
            -- Canonical claim rows (permalink-url-design.md 3.6.11 "Canonical
            -- claims"): the election key is (domain, capsule_id, binding_key,
            -- predecessor_event_id), where binding_key is the empty string in
            -- the content domain and the binding instance id in the path
            -- domain. Shadow-filled by PermalinkCanonicalClaimMigration; no
            -- election path reads or writes this table yet.
            CREATE TABLE IF NOT EXISTS CanonicalTransitionClaims (
                domain TEXT NOT NULL, capsule_id TEXT NOT NULL,
                binding_key TEXT NOT NULL DEFAULT '',
                predecessor_event_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                proposed_decision TEXT NOT NULL, proposed_event_id TEXT NOT NULL,
                canonical_event_digest TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(domain, capsule_id, binding_key, predecessor_event_id));
            -- Root-to-event mapping recorded for every legacy MutationClaims
            -- row the migration folds (permalink-orphan-resolution-design-
            -- 20260913.md P1 step 2). Path-kind rows stay unmapped until
            -- their own slice; unmappable rows are quarantined as contested.
            CREATE TABLE IF NOT EXISTS LegacyPredecessorMap (
                capsule_id TEXT NOT NULL, predecessor_root TEXT NOT NULL,
                mapped_event_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(capsule_id, predecessor_root));
            CREATE TABLE IF NOT EXISTS PermalinkBindings (
                PermalinkId TEXT NOT NULL, ItemId TEXT NOT NULL,
                ContentRoot TEXT NOT NULL, VerifiedToken TEXT NOT NULL,
                VerifiedAt TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                PRIMARY KEY(PermalinkId, ItemId));
            CREATE INDEX IF NOT EXISTS IX_PermalinkBindings_ItemId
                ON PermalinkBindings(ItemId);
            -- Durable resume state for the one-time operations/ walk that
            -- discovers pre-existing pending operations, and the running
            -- total this row keeps afterward. last_operation_id is the
            -- ordinal-sorted cursor the walk has verified up to; completed_at
            -- switches every future boot from that walk onto the O(pending)
            -- marker-directory listing for good. See
            -- PermalinkOperationJournal.MigratePendingSetAsync.
            CREATE TABLE IF NOT EXISTS PermalinkPendingMigration (
                name TEXT PRIMARY KEY, last_operation_id TEXT NULL,
                directories_seen INTEGER NOT NULL, completed_at TEXT NULL,
                created_at TEXT NOT NULL);
            -- Future terminal writes enqueue here before their immutable phase is
            -- published. The archiver can therefore select a small indexed batch
            -- without repeatedly enumerating the flat operations directory.
            CREATE TABLE IF NOT EXISTS PermalinkOperationArchiveCandidates (
                operation_id TEXT PRIMARY KEY, terminal_phase TEXT NOT NULL,
                terminal_at TEXT NOT NULL, next_attempt_at TEXT NOT NULL,
                attempts INTEGER NOT NULL, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PermalinkOperationArchiveCandidates_Due
                ON PermalinkOperationArchiveCandidates(next_attempt_at, created_at, operation_id);
            -- FirstAliasClaims is keyed by capsule_id, so every lookup BY ALIAS
            -- was a full scan: the resolver's own binding join does one, and so
            -- does the competing-claims test in BindAsync, which runs for every
            -- alias of every ensure. Without this the repeat-ensure caching test
            -- fails outright on this host.
            CREATE INDEX IF NOT EXISTS IX_FirstAliasClaims_PermalinkId
                ON FirstAliasClaims(permalink_id);
            CREATE TRIGGER IF NOT EXISTS protect_permalink_issuances_update
            BEFORE UPDATE ON PermalinkIssuances BEGIN
                SELECT CASE WHEN (SELECT enforcement_enabled FROM AuthorityWriteProtection WHERE singleton = 1) = 1
                    THEN RAISE(ABORT, 'permalink-protected-immutable') END;
            END;
            CREATE TRIGGER IF NOT EXISTS protect_permalink_issuances_delete
            BEFORE DELETE ON PermalinkIssuances BEGIN
                SELECT CASE WHEN (SELECT enforcement_enabled FROM AuthorityWriteProtection WHERE singleton = 1) = 1
                    THEN RAISE(ABORT, 'permalink-protected-immutable') END;
            END;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InstallWriteProtectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallWriteProtectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new (string Table, string Key)[]
        {
            ("AuthorityProtocolState", "CAST(NEW.singleton AS TEXT)"),
            ("SnapshotMetadata", "CAST(NEW.generation AS TEXT)"),
            ("AnchorTokenCapsules", "NEW.anchor_token"),
            ("PermalinkIssuances", "NEW.PermalinkId"),
            ("FirstAliasClaims", "NEW.capsule_id"),
            ("PermalinkBindingCapsuleOverrides", "NEW.PermalinkId || char(0) || NEW.ItemId"),
            ("ContentTransitionClaims", "NEW.capsule_id || char(0) || NEW.predecessor"),
            ("OperationDecisions", "NEW.operation_id || char(0) || NEW.capsule_id || char(0) || NEW.predecessor"),
            ("PathOperationDecisions", "NEW.operation_id || char(0) || NEW.capsule_id || char(0) || NEW.binding_id || char(0) || NEW.predecessor"),
            ("MutationClaims", "NEW.capsule_id || char(0) || NEW.predecessor_root"),
            ("CanonicalTransitionClaims", "NEW.domain || char(0) || NEW.capsule_id || char(0) || NEW.binding_key || char(0) || NEW.predecessor_event_id"),
            ("LegacyPredecessorMap", "NEW.capsule_id || char(0) || NEW.predecessor_root"),
            ("PermalinkBindings", "NEW.PermalinkId || char(0) || NEW.ItemId"),
            ("PermalinkPendingMigration", "NEW.name"),
            ("PermalinkOperationArchiveCandidates", "NEW.operation_id")
        };
        foreach (var (table, newKey) in tables)
        {
            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                if (table == "PermalinkIssuances" && verb is "UPDATE" or "DELETE")
                {
                    continue;
                }

                var key = verb == "DELETE" ? newKey.Replace("NEW.", "OLD.", StringComparison.Ordinal) : newKey;
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS protect_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    BEFORE {verb} ON {table}
                    WHEN (SELECT enforcement_enabled FROM AuthorityWriteProtection WHERE singleton = 1) = 1
                    BEGIN
                        SELECT CASE WHEN authority_capability(
                            '{table}', '{verb}', {key},
                            COALESCE((SELECT mac FROM AuthorityWriteAuthorizations
                                WHERE table_name = '{table}' AND verb = '{verb}'
                                  AND primary_key = {key}), '')) != 1
                            THEN RAISE(ABORT, 'permalink-protected-write') END;
                    END;
                    """;
                _ = await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var table in tables.Select(entry => entry.Table).Where(table => table != "SnapshotMetadata"))
        {
            var journalIncrement = table is "OperationDecisions" or "PathOperationDecisions"
                ? "journal_counter + 1"
                : "journal_counter";
            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                await using var counter = connection.CreateCommand();
                counter.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS count_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    AFTER {verb} ON {table} BEGIN
                        UPDATE AuthorityCounters SET
                            authority_sequence = authority_sequence + 1,
                            content_epoch = content_epoch + 1,
                            journal_counter = {journalIncrement}
                        WHERE singleton = 1;
                    END;
                    """;
                _ = await counter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Inserts the complete authorized null-predecessor genesis decision set.
    /// </summary>
    /// <param name="connection">The SQLite authority connection.</param>
    /// <param name="transaction">The active SQLite transaction.</param>
    /// <param name="candidate">The candidate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task InsertGenesisClaimsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkGenesisCandidate candidate,
        CancellationToken cancellationToken)
    {
        var digest = CanonicalJson.Digest(candidate.EventJson);
        var eventId = candidate.Event.EventId.ToString("D");
        var operationId = candidate.Event.OperationId.ToString("D");
        var capsuleId = candidate.CapsuleId.ToString("D");
        var bindingId = candidate.BindingId.ToString("D");
        var commands = new[]
        {
            "INSERT INTO ContentTransitionClaims VALUES ($capsule, 'genesis', $operation, $event, $digest, $created)",
            "INSERT INTO OperationDecisions VALUES ($operation, $capsule, 'genesis', 'ready', $event, $digest, $created)",
            "INSERT INTO PathOperationDecisions VALUES ($operation, $capsule, $binding, 'genesis', 'initialized', $event, $digest, $created)"
        };
        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$capsule", capsuleId);
            command.Parameters.AddWithValue("$operation", operationId);
            command.Parameters.AddWithValue("$event", eventId);
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$created", candidate.CreatedAt);
            command.Parameters.AddWithValue("$binding", bindingId);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var firstAlias = candidate.Issuance?.Id
            ?? candidate.Event.ActiveExternalAliases.FirstOrDefault();
        if (firstAlias is null)
        {
            return;
        }

        if (candidate.Issuance is not null)
        {
            await using var issuance = connection.CreateCommand();
            issuance.Transaction = transaction;
            issuance.CommandText = """
                INSERT INTO PermalinkIssuances VALUES ($id, $nonce, $created, $created);
                """;
            issuance.Parameters.AddWithValue("$id", candidate.Issuance.Id);
            issuance.Parameters.AddWithValue(
                "$nonce",
                candidate.Issuance.IssuanceNonce.ToString("D"));
            issuance.Parameters.AddWithValue("$created", candidate.CreatedAt);
            _ = await issuance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var alias = connection.CreateCommand();
        alias.Transaction = transaction;
        alias.CommandText = """
            INSERT INTO FirstAliasClaims VALUES
                ($capsule, $id, $nonce, $event, $digest, $json, $created);
            """;
        alias.Parameters.AddWithValue("$capsule", capsuleId);
        alias.Parameters.AddWithValue("$id", firstAlias);
        alias.Parameters.AddWithValue(
            "$nonce",
            candidate.Issuance?.IssuanceNonce.ToString("D") ?? (object)DBNull.Value);
        alias.Parameters.AddWithValue("$event", eventId);
        alias.Parameters.AddWithValue("$digest", digest);
        alias.Parameters.AddWithValue(
            "$json",
            System.Text.Encoding.UTF8.GetString(candidate.EventJson.Span));
        alias.Parameters.AddWithValue("$created", candidate.CreatedAt);
        _ = await alias.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
