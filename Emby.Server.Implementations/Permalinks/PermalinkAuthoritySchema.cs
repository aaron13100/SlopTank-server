// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-03, 2026-09-05, 2026-09-08, 2026-09-09, 2026-09-14, 2026-09-19, 2026-10-01, 2026-10-02, 2026-10-03, 2026-10-04, 2026-10-07, 2026-10-09, 2026-10-10, 2026-10-14.
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
            INSERT INTO AuthorityProtocolState
                (singleton, protocol_epoch, irreversible_floor, updated_at)
            SELECT 1, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE NOT EXISTS (
                SELECT 1 FROM AuthorityProtocolState WHERE singleton = 1);
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
            CREATE TRIGGER IF NOT EXISTS protect_authority_write_protection_update
            BEFORE UPDATE ON AuthorityWriteProtection BEGIN
                SELECT CASE WHEN authority_counter_write() != 1
                    THEN RAISE(ABORT, 'permalink-protection-control-write') END;
            END;
            CREATE TRIGGER IF NOT EXISTS protect_authority_write_protection_delete
            BEFORE DELETE ON AuthorityWriteProtection BEGIN
                SELECT RAISE(ABORT, 'permalink-protection-control-immutable');
            END;
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
            CREATE TABLE IF NOT EXISTS PermalinkRootIdentities (
                root_identity_id TEXT PRIMARY KEY,
                generation INTEGER NOT NULL CHECK(generation > 0),
                canonical_root TEXT NOT NULL,
                attestation TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(canonical_root, generation));
            CREATE TABLE IF NOT EXISTS PermalinkEvidenceRegistry (
                registry_id TEXT PRIMARY KEY,
                evidence_kind TEXT NOT NULL,
                root_identity_id TEXT NOT NULL,
                normalized_relative_path TEXT NOT NULL,
                operation_id TEXT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                anchor_token TEXT NULL,
                FOREIGN KEY(root_identity_id) REFERENCES PermalinkRootIdentities(root_identity_id));
            -- A population run is control evidence, not content. Registry writes performed by the
            -- run advance content_epoch; completing this row records the exact epoch/cardinality
            -- after the last such write without invalidating itself.
            CREATE TABLE IF NOT EXISTS PermalinkRegistryPopulations (
                population_id TEXT PRIMARY KEY,
                registry_writer_version INTEGER NOT NULL CHECK(registry_writer_version > 0),
                status TEXT NOT NULL CHECK(status IN ('running', 'complete', 'failed')),
                completed_content_epoch INTEGER NULL CHECK(completed_content_epoch >= 0),
                registry_cardinality INTEGER NULL CHECK(registry_cardinality >= 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CHECK((status = 'complete' AND completed_content_epoch IS NOT NULL AND registry_cardinality IS NOT NULL)
                    OR (status <> 'complete' AND completed_content_epoch IS NULL AND registry_cardinality IS NULL)));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PermalinkRegistryPopulations_Running
                ON PermalinkRegistryPopulations(status) WHERE status = 'running';
            CREATE TABLE IF NOT EXISTS PermalinkRegistryPopulationCheckpoints (
                population_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                source_key TEXT NOT NULL,
                source_generation TEXT NOT NULL,
                cursor_version INTEGER NOT NULL CHECK(cursor_version > 0),
                cursor TEXT NULL,
                observed_count INTEGER NOT NULL CHECK(observed_count >= 0),
                status TEXT NOT NULL CHECK(status IN ('pending', 'running', 'complete', 'failed')),
                version INTEGER NOT NULL DEFAULT 1 CHECK(version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                PRIMARY KEY(population_id, source_kind, source_key),
                FOREIGN KEY(population_id) REFERENCES PermalinkRegistryPopulations(population_id));
            -- Watermarks are append-only seals over one completed population plus both complete
            -- operation-journal areas. They do not mutate the content facts they attest.
            CREATE TABLE IF NOT EXISTS PermalinkAbsenceWatermarks (
                watermark_id TEXT PRIMARY KEY,
                root_generations_json TEXT NOT NULL,
                hot_journal_generation TEXT NOT NULL,
                hot_journal_cardinality INTEGER NOT NULL CHECK(hot_journal_cardinality >= 0),
                archive_journal_generation TEXT NOT NULL,
                archive_journal_cardinality INTEGER NOT NULL CHECK(archive_journal_cardinality >= 0),
                registry_writer_version INTEGER NOT NULL CHECK(registry_writer_version > 0),
                content_epoch INTEGER NOT NULL CHECK(content_epoch >= 0),
                journal_counter INTEGER NOT NULL CHECK(journal_counter >= 0),
                registry_cardinality INTEGER NOT NULL CHECK(registry_cardinality >= 0),
                population_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(population_id) REFERENCES PermalinkRegistryPopulations(population_id));
            CREATE TABLE IF NOT EXISTS PermalinkWriterAdmissions (
                admission_id TEXT PRIMARY KEY,
                root_identity_id TEXT NOT NULL,
                normalized_relative_path TEXT NOT NULL,
                pinned_object_identity TEXT NOT NULL,
                action TEXT NOT NULL,
                snapshot_generation INTEGER NOT NULL,
                authority_epoch INTEGER NOT NULL,
                content_epoch INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('issued', 'started', 'completed', 'recovery-required')),
                start_deadline_utc TEXT NOT NULL,
                heartbeat_deadline_utc TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(root_identity_id) REFERENCES PermalinkRootIdentities(root_identity_id));
            -- P9 elevated confirmation authority. The plaintext server challenge is returned
            -- once and never stored; only its canonical SHA-256 digest crosses this boundary.
            CREATE TABLE IF NOT EXISTS PermalinkConfirmationChallenges (
                challenge_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL UNIQUE,
                challenge_digest TEXT NOT NULL
                    CHECK(length(challenge_digest) = 64 AND challenge_digest NOT GLOB '*[^0-9a-f]*'),
                target_digest TEXT NOT NULL
                    CHECK(length(target_digest) = 64 AND target_digest NOT GLOB '*[^0-9a-f]*'),
                inventory_generation TEXT NOT NULL
                    CHECK(length(inventory_generation) = 64 AND inventory_generation NOT GLOB '*[^0-9a-f]*'),
                requested_action TEXT NOT NULL CHECK(requested_action IN (
                    'orphan-closure', 'exact-restoration', 'restore-with-rewrite',
                    'successor-mint', 'detached-capsule-recovery', 'scratch-adoption')),
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK(version > 0),
                kind TEXT NOT NULL DEFAULT 'confirmation' CHECK(kind = 'confirmation'),
                deleted_at TEXT NULL,
                CHECK(expires_at > created_at),
                CHECK(consumed_at IS NULL OR consumed_at >= created_at));
            -- A retirement copy is a two-root operation. Its registry row owns the content fact,
            -- its writer-admission row reserves the source content key, and this companion binds
            -- the exact absent evidence destination and its parent without overloading either
            -- older one-path schema.
            CREATE TABLE IF NOT EXISTS PermalinkRetirementIntents (
                operation_id TEXT PRIMARY KEY,
                registry_id TEXT NOT NULL UNIQUE,
                admission_id TEXT NOT NULL UNIQUE,
                destination_root_identity_id TEXT NOT NULL,
                destination_normalized_relative_path TEXT NOT NULL,
                destination_parent_pinned_object_identity TEXT NOT NULL,
                snapshot_generation INTEGER NOT NULL CHECK(snapshot_generation > 0),
                published_object_identity TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(registry_id) REFERENCES PermalinkEvidenceRegistry(registry_id),
                FOREIGN KEY(admission_id) REFERENCES PermalinkWriterAdmissions(admission_id),
                FOREIGN KEY(destination_root_identity_id) REFERENCES PermalinkRootIdentities(root_identity_id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PermalinkRetirementIntents_Destination
                ON PermalinkRetirementIntents(destination_root_identity_id, destination_normalized_relative_path);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PermalinkEvidenceRegistry_PendingScanKey
                ON PermalinkEvidenceRegistry(root_identity_id, normalized_relative_path)
                WHERE evidence_kind = 'pending-scan' AND state = 'pending';
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PermalinkWriterAdmissions_ActivePath
                ON PermalinkWriterAdmissions(root_identity_id, normalized_relative_path)
                WHERE status IN ('issued', 'started', 'recovery-required');
            -- Path reservations (P6): one ACTIVE row per (root, path) key. Rows are never
            -- deleted; a released or consumed row stays so MAX(generation) for the key
            -- exposes an insert-then-release that happened inside an evidence gap.
            CREATE TABLE IF NOT EXISTS PathReservations (
                reservation_id TEXT PRIMARY KEY,
                root_identity_id TEXT NOT NULL,
                normalized_relative_path TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('restore', 'successor', 'path-mutation')),
                operation_id TEXT NOT NULL,
                capsule_id TEXT NULL,
                status TEXT NOT NULL CHECK(status IN ('active', 'consumed', 'released')),
                generation INTEGER NOT NULL CHECK(generation > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(root_identity_id) REFERENCES PermalinkRootIdentities(root_identity_id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PathReservations_ActiveKey
                ON PathReservations(root_identity_id, normalized_relative_path)
                WHERE status = 'active';
            CREATE INDEX IF NOT EXISTS IX_PathReservations_KeyGeneration
                ON PathReservations(root_identity_id, normalized_relative_path, generation);
            -- Pending supersession slots (P6): a detached capsule's anchor waiting for a
            -- successor at the key. One pending slot per key; one slot per (capsule, anchor).
            CREATE TABLE IF NOT EXISTS PendingSupersessionSlots (
                slot_id TEXT PRIMARY KEY,
                root_identity_id TEXT NOT NULL,
                normalized_relative_path TEXT NOT NULL,
                detached_capsule_id TEXT NOT NULL,
                anchor_token TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('pending', 'consumed')),
                consumed_by_operation_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(detached_capsule_id, anchor_token),
                FOREIGN KEY(root_identity_id) REFERENCES PermalinkRootIdentities(root_identity_id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PendingSupersessionSlots_PendingKey
                ON PendingSupersessionSlots(root_identity_id, normalized_relative_path)
                WHERE status = 'pending';
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

        // CREATE TABLE IF NOT EXISTS never alters an existing table, so a database created before
        // the anchor-intent token existed gets the column added in place (a no-op once present).
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "anchor_token", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "population_id", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "source_key", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "pinned_object_identity", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "content_length", "INTEGER NULL CHECK(content_length >= 0)", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "content_sha256", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "record_version", "INTEGER NOT NULL DEFAULT 1 CHECK(record_version > 0)", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "superseded_by_registry_id", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, "PermalinkEvidenceRegistry", "deleted_at", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await using (var evidenceIndex = connection.CreateCommand())
        {
            evidenceIndex.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_PermalinkEvidenceRegistry_ActivePopulationSource
                    ON PermalinkEvidenceRegistry(evidence_kind, root_identity_id, source_key)
                    WHERE source_key IS NOT NULL AND superseded_by_registry_id IS NULL AND deleted_at IS NULL
                """;
            _ = await evidenceIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InstallConfirmationChallengeDigestValidationAsync(connection, cancellationToken).ConfigureAwait(false);
        await InstallImmutableEvidenceAsync(connection, cancellationToken).ConfigureAwait(false);
        await InstallWriteProtectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallConfirmationChallengeDigestValidationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var verb in new[] { "INSERT", "UPDATE" })
        {
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS validate_permalink_confirmation_challenges_digests_{verb.ToLowerInvariant()}
                BEFORE {verb} ON PermalinkConfirmationChallenges
                WHEN NEW.challenge_digest IS NULL
                    OR typeof(NEW.challenge_digest) != 'text'
                    OR length(CAST(NEW.challenge_digest AS BLOB)) != 64
                    OR length(NEW.challenge_digest) != 64
                    OR NEW.challenge_digest GLOB '*[^0-9a-f]*'
                    OR NEW.target_digest IS NULL
                    OR typeof(NEW.target_digest) != 'text'
                    OR length(CAST(NEW.target_digest AS BLOB)) != 64
                    OR length(NEW.target_digest) != 64
                    OR NEW.target_digest GLOB '*[^0-9a-f]*'
                    OR NEW.inventory_generation IS NULL
                    OR typeof(NEW.inventory_generation) != 'text'
                    OR length(CAST(NEW.inventory_generation AS BLOB)) != 64
                    OR length(NEW.inventory_generation) != 64
                    OR NEW.inventory_generation GLOB '*[^0-9a-f]*'
                BEGIN
                    SELECT RAISE(ABORT, 'permalink-confirmation-challenge-digest-invalid');
                END;
                """;
            _ = await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
        probe.Parameters.AddWithValue("$table", table);
        probe.Parameters.AddWithValue("$column", column);
        var present = Convert.ToInt64(
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (present > 0)
        {
            return;
        }

        // Identifiers are compile-time constants of this class, never caller input.
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        _ = await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InstallImmutableEvidenceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string[] tables =
        [
            "SnapshotMetadata",
            "PermalinkIssuances",
            "FirstAliasClaims",
            "ContentTransitionClaims",
            "OperationDecisions",
            "PathOperationDecisions",
            "MutationClaims",
            "CanonicalTransitionClaims",
            "LegacyPredecessorMap",
            "PermalinkAbsenceWatermarks"
        ];
        foreach (var table in tables)
        {
            foreach (var verb in new[] { "UPDATE", "DELETE" })
            {
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS immutable_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    BEFORE {verb} ON {table} BEGIN
                        SELECT RAISE(ABORT, 'permalink-immutable-evidence');
                    END;
                    """;
                _ = await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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
            ("PermalinkOperationArchiveCandidates", "NEW.operation_id"),
            ("PermalinkRootIdentities", "NEW.root_identity_id"),
            ("PermalinkEvidenceRegistry", "NEW.registry_id"),
            ("PermalinkRegistryPopulations", "NEW.population_id"),
            ("PermalinkRegistryPopulationCheckpoints", "NEW.population_id || char(0) || NEW.source_kind || char(0) || NEW.source_key"),
            ("PermalinkAbsenceWatermarks", "NEW.watermark_id"),
            ("PermalinkWriterAdmissions", "NEW.admission_id"),
            ("PermalinkConfirmationChallenges", "NEW.challenge_id"),
            ("PermalinkRetirementIntents", "NEW.operation_id"),
            ("PathReservations", "NEW.reservation_id"),
            ("PendingSupersessionSlots", "NEW.slot_id")
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

        foreach (var table in tables.Select(entry => entry.Table).Where(
                     table => table is not "SnapshotMetadata"
                         and not "PermalinkRegistryPopulations"
                         and not "PermalinkRegistryPopulationCheckpoints"
                         and not "PermalinkAbsenceWatermarks"))
        {
            var journalIncrement = table is "OperationDecisions" or "PathOperationDecisions"
                ? "journal_counter + 1"
                : "journal_counter";

            // Writer admissions are lease bookkeeping, not content: they advance
            // authority_sequence but never content_epoch, so heartbeats cannot cause
            // content-epoch conflicts for unrelated paths or stop the absence watermark
            // from converging.
            // The retirement companion and admission are binding/lease details inserted in the
            // same transaction as their registry row. The registry write is the one content fact
            // and advances content_epoch exactly once.
            var contentIncrement = table is "PermalinkWriterAdmissions"
                or "PermalinkRetirementIntents"
                or "PermalinkConfirmationChallenges"
                ? "content_epoch"
                : "content_epoch + 1";
            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var counterName = $"count_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}";
                if (contentIncrement == "content_epoch")
                {
                    // CREATE TRIGGER IF NOT EXISTS never replaces a trigger, so a database that
                    // already carries the content-bumping definition must be repaired in place.
                    await using var drop = connection.CreateCommand();
                    drop.CommandText = $"DROP TRIGGER IF EXISTS {counterName}";
                    _ = await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var counter = connection.CreateCommand();
                counter.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS {counterName}
                    AFTER {verb} ON {table} BEGIN
                        UPDATE AuthorityCounters SET
                            authority_sequence = authority_sequence + 1,
                            content_epoch = {contentIncrement},
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
        var pathEventId = candidate.Event.PathAssignment.EventId.ToString("D");
        var commands = new[]
        {
            (Sql: "INSERT INTO ContentTransitionClaims VALUES ($capsule, 'genesis', $operation, $event, $digest, $created)", EventId: eventId),
            (Sql: "INSERT INTO OperationDecisions VALUES ($operation, $capsule, 'genesis', 'ready', $event, $digest, $created)", EventId: eventId),
            (Sql: "INSERT INTO PathOperationDecisions VALUES ($operation, $capsule, $binding, 'genesis', 'initialized', $event, $digest, $created)", EventId: pathEventId)
        };
        foreach (var commandDefinition in commands)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandDefinition.Sql;
            command.Parameters.AddWithValue("$capsule", capsuleId);
            command.Parameters.AddWithValue("$operation", operationId);
            command.Parameters.AddWithValue("$event", commandDefinition.EventId);
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
