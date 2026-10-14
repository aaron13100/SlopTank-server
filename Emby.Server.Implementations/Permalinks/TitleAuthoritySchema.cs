// SlopTank modification notice: added or changed by SlopTank on 2026-10-14.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Declares the additive title-identity authority records and their SQLite constraints.</summary>
internal static class TitleAuthoritySchema
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Titles (
            title_id TEXT NOT NULL PRIMARY KEY CHECK(typeof(title_id) = 'text' AND length(CAST(title_id AS BLOB)) = 36 AND length(title_id) = 36 AND substr(title_id,9,1) = '-' AND substr(title_id,14,1) = '-' AND substr(title_id,19,1) = '-' AND substr(title_id,24,1) = '-' AND length(replace(title_id,'-','')) = 32 AND replace(title_id,'-','') NOT GLOB '*[^0-9a-f]*'),
            entity_kind TEXT NOT NULL CHECK(entity_kind IN ('movie','series','season','episode','boxset','video','musicvideo')),
            revision INTEGER NOT NULL CHECK(revision > 0), state TEXT NOT NULL CHECK(state IN ('active','unavailable','removed','unresolved')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS TitleOperationRecords (
            operation_id TEXT NOT NULL PRIMARY KEY, command_kind TEXT NOT NULL, request_version INTEGER NOT NULL CHECK(request_version = 2), request_json TEXT NOT NULL,
            request_digest TEXT NOT NULL, actor_id TEXT NOT NULL, capability_scope_digest TEXT NOT NULL, expected_heads_json TEXT NOT NULL,
            desired_sets_json TEXT NOT NULL, desired_manifest_digest TEXT NULL, phase_event_id TEXT NOT NULL, result_digest TEXT NULL,
            recovery_cursor TEXT NOT NULL, failure_code TEXT NULL, failure_detail TEXT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            UNIQUE(operation_id, phase_event_id), FOREIGN KEY(operation_id, phase_event_id) REFERENCES TitleOperationPhaseEvents(operation_id, event_id) ON UPDATE RESTRICT ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED);
        CREATE TABLE IF NOT EXISTS TitleOperationPhaseEvents (
            event_id TEXT NOT NULL PRIMARY KEY, operation_id TEXT NOT NULL, predecessor_event_id TEXT NULL,
            phase TEXT NOT NULL CHECK(phase IN ('prepared','evidence-validated','authority-decided','effects-applied','heads-published','projections-published','complete','aborted','refused')),
            evidence_digest TEXT NOT NULL, event_digest TEXT NOT NULL, no_effect_reason TEXT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            UNIQUE(operation_id, event_id), FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id, predecessor_event_id) REFERENCES TitleOperationPhaseEvents(operation_id, event_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleOperationDecisions (
            operation_id TEXT NOT NULL PRIMARY KEY, command_kind TEXT NOT NULL, request_digest TEXT NOT NULL, actor_id TEXT NOT NULL,
            capability_digest TEXT NOT NULL, expected_revision_digest TEXT NOT NULL, decision TEXT NOT NULL CHECK(decision IN ('committed','refused','aborted')),
            result_digest TEXT NOT NULL, journal_event_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id, journal_event_id) REFERENCES TitleOperationPhaseEvents(operation_id, event_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasClaims (
            alias TEXT COLLATE BINARY NOT NULL PRIMARY KEY, initial_title_id TEXT NOT NULL, alias_kind TEXT NOT NULL CHECK(alias_kind IN ('opaque','provider')),
            issuance_nonce TEXT NULL, origin_claim_id TEXT NOT NULL, initial_observation_digest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('reserved','issued')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(initial_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasResolutionEvents (
            event_id TEXT NOT NULL PRIMARY KEY, alias TEXT COLLATE BINARY NOT NULL, predecessor_event_id TEXT NULL, target_title_id TEXT NULL,
            operation_id TEXT NOT NULL, evidence_digest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('resolved','contested')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            UNIQUE(alias,event_id), CHECK((state='resolved' AND target_title_id IS NOT NULL) OR (state='contested' AND target_title_id IS NULL)),
            FOREIGN KEY(alias) REFERENCES TitleAliasClaims(alias) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(alias,predecessor_event_id) REFERENCES TitleAliasResolutionEvents(alias,event_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(target_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasHeads (
            alias TEXT COLLATE BINARY NOT NULL PRIMARY KEY, event_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(alias) REFERENCES TitleAliasClaims(alias) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(alias,event_id) REFERENCES TitleAliasResolutionEvents(alias,event_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasOrderSets (
            title_id TEXT NOT NULL, order_revision INTEGER NOT NULL CHECK(order_revision > 0), operation_id TEXT NOT NULL,
            alias_count INTEGER NOT NULL CHECK(alias_count > 0), set_digest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('prepared','complete')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(title_id,order_revision), FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasOrders (
            title_id TEXT NOT NULL, order_revision INTEGER NOT NULL, ordinal INTEGER NOT NULL CHECK(ordinal >= 0), alias TEXT COLLATE BINARY NOT NULL,
            operation_id TEXT NOT NULL, record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(title_id,order_revision,ordinal), UNIQUE(title_id,order_revision,alias),
            FOREIGN KEY(title_id,order_revision) REFERENCES TitleAliasOrderSets(title_id,order_revision) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(alias) REFERENCES TitleAliasClaims(alias) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleAliasOrderHeads (
            title_id TEXT NOT NULL PRIMARY KEY, order_revision INTEGER NOT NULL, operation_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(title_id,order_revision) REFERENCES TitleAliasOrderSets(title_id,order_revision) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS LegacyAliasResponseOrders (
            context_kind TEXT NOT NULL, context_id TEXT NOT NULL, ordinal INTEGER NOT NULL CHECK(ordinal >= 0), alias TEXT COLLATE BINARY NOT NULL,
            was_canonical INTEGER NOT NULL CHECK(was_canonical IN (0,1)), observation_digest TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(context_kind,context_id,ordinal), FOREIGN KEY(alias) REFERENCES TitleAliasClaims(alias) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LegacyAliasResponseOrders_Canonical ON LegacyAliasResponseOrders(context_kind,context_id) WHERE was_canonical=1;
        CREATE TABLE IF NOT EXISTS TitleVersions (
            version_id TEXT NOT NULL PRIMARY KEY, title_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0),
            shape TEXT NOT NULL CHECK(shape IN ('single','multipart','optical')),
            state TEXT NOT NULL CHECK(state IN ('pending','ready','unavailable','retired','revoked')), current_generation_id TEXT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            UNIQUE(version_id,title_id), FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(version_id,current_generation_id) REFERENCES VersionGenerations(version_id,generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS VersionGenerations (
            generation_id TEXT NOT NULL PRIMARY KEY, version_id TEXT NOT NULL, generation_ordinal INTEGER NOT NULL CHECK(generation_ordinal > 0),
            capsule_id TEXT NOT NULL, content_event_id TEXT NOT NULL, path_event_id TEXT NOT NULL, manifest_digest TEXT NOT NULL,
            manifest_component_count INTEGER NOT NULL CHECK(manifest_component_count > 0), state TEXT NOT NULL CHECK(state IN ('prepared','ready','superseded','quarantined')),
            probe_result_digest TEXT NOT NULL, admission_operation_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            UNIQUE(version_id,generation_ordinal), UNIQUE(version_id,generation_id),
            FOREIGN KEY(version_id) REFERENCES TitleVersions(version_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(admission_operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS GenerationComponents (
            generation_id TEXT NOT NULL, component_key TEXT NOT NULL, ordinal INTEGER NOT NULL CHECK(ordinal >= 0), role TEXT NOT NULL,
            tree_relative_path TEXT NULL, content_digest TEXT NOT NULL, byte_length INTEGER NOT NULL CHECK(byte_length >= 0), root_id TEXT NOT NULL,
            canonical_path TEXT NOT NULL, object_token TEXT NOT NULL, binding_instance_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(generation_id,component_key), UNIQUE(generation_id,ordinal),
            FOREIGN KEY(generation_id) REFERENCES VersionGenerations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(root_id) REFERENCES PermalinkRootIdentities(root_identity_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            CHECK((component_key='main' AND tree_relative_path IS NULL) OR (component_key LIKE 'part:%' AND tree_relative_path IS NULL) OR (tree_relative_path IS NOT NULL AND component_key=tree_relative_path)));
        CREATE UNIQUE INDEX IF NOT EXISTS UX_GenerationComponents_OpticalCase ON GenerationComponents(generation_id,lower(tree_relative_path)) WHERE tree_relative_path IS NOT NULL;
        CREATE TABLE IF NOT EXISTS GenerationEvidenceRecords (
            generation_id TEXT NOT NULL, evidence_role TEXT NOT NULL CHECK(evidence_role IN ('content-event','path-event','binding','probe','admission')),
            source_record_type TEXT NOT NULL, source_record_version INTEGER NOT NULL CHECK(source_record_version > 0), source_record_digest TEXT NOT NULL,
            capsule_id TEXT NULL, binding_instance_id TEXT NULL, event_id TEXT NULL, operation_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(generation_id,evidence_role), FOREIGN KEY(generation_id) REFERENCES VersionGenerations(generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            CHECK(evidence_role NOT IN ('content-event','path-event','binding') OR (capsule_id IS NOT NULL AND binding_instance_id IS NOT NULL AND event_id IS NOT NULL)));
        CREATE TABLE IF NOT EXISTS AcceptedTitleClaims (
            claim_id TEXT NOT NULL PRIMARY KEY, title_id TEXT NOT NULL, namespace TEXT NOT NULL, entity_kind TEXT NOT NULL,
            scope_key TEXT NOT NULL, value TEXT COLLATE BINARY NOT NULL, parent_title_id TEXT NULL, provenance TEXT NOT NULL CHECK(provenance IN ('authenticated-identification','authenticated-ingest-intent','legacy-authority-migration')),
            acceptance_operation_id TEXT NOT NULL, accepted_title_revision INTEGER NOT NULL CHECK(accepted_title_revision > 0), state TEXT NOT NULL CHECK(state IN ('accepted','revoked','contested')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(parent_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(acceptance_operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            CHECK((parent_title_id IS NULL AND scope_key='root') OR (parent_title_id IS NOT NULL AND scope_key=parent_title_id)));
        CREATE UNIQUE INDEX IF NOT EXISTS UX_AcceptedTitleClaims_Scope ON AcceptedTitleClaims(namespace,entity_kind,scope_key,value) WHERE state='accepted';
        CREATE TABLE IF NOT EXISTS TitleRelationSets (
            parent_title_id TEXT NOT NULL, relation_revision INTEGER NOT NULL CHECK(relation_revision > 0), operation_id TEXT NOT NULL,
            child_count INTEGER NOT NULL CHECK(child_count >= 0), set_digest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('prepared','complete')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(parent_title_id,relation_revision), FOREIGN KEY(parent_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleRelations (
            parent_title_id TEXT NOT NULL, relation_revision INTEGER NOT NULL, child_title_id TEXT NOT NULL,
            relation_kind TEXT NOT NULL CHECK(relation_kind IN ('season','episode','collection-member')), ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            season_number INTEGER NULL CHECK(season_number IS NULL OR season_number >= 0), episode_number INTEGER NULL CHECK(episode_number IS NULL OR episode_number >= 0),
            operation_id TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('active','contested')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(parent_title_id,relation_revision,child_title_id), UNIQUE(parent_title_id,relation_revision,ordinal),
            FOREIGN KEY(parent_title_id,relation_revision) REFERENCES TitleRelationSets(parent_title_id,relation_revision) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(parent_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(child_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            CHECK((relation_kind='season' AND season_number IS NOT NULL AND episode_number IS NULL) OR
                (relation_kind='episode' AND season_number IS NOT NULL AND episode_number IS NOT NULL) OR
                (relation_kind='collection-member' AND season_number IS NULL AND episode_number IS NULL)));
        CREATE TABLE IF NOT EXISTS TitleRelationHeads (
            parent_title_id TEXT NOT NULL PRIMARY KEY, relation_revision INTEGER NOT NULL, operation_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(parent_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(parent_title_id,relation_revision) REFERENCES TitleRelationSets(parent_title_id,relation_revision) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleEquivalenceEvents (
            equivalence_event_id TEXT NOT NULL, source_title_id TEXT NOT NULL, target_title_id TEXT NOT NULL, predecessor_event_id TEXT NULL,
            operation_id TEXT NOT NULL, partition_digest TEXT NOT NULL, evidence_digest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('merged','revoked')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(source_title_id,equivalence_event_id), UNIQUE(source_title_id,equivalence_event_id),
            FOREIGN KEY(source_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(target_title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(source_title_id,predecessor_event_id) REFERENCES TitleEquivalenceEvents(source_title_id,equivalence_event_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            CHECK(source_title_id != target_title_id));
        CREATE TABLE IF NOT EXISTS TitleEquivalenceHeads (
            source_title_id TEXT NOT NULL PRIMARY KEY, equivalence_event_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0), operation_id TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(source_title_id,equivalence_event_id) REFERENCES TitleEquivalenceEvents(source_title_id,equivalence_event_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleItemAssociations (
            version_id TEXT NOT NULL, item_id TEXT NOT NULL, title_id TEXT NOT NULL, generation_id TEXT NOT NULL,
            title_revision INTEGER NOT NULL CHECK(title_revision > 0), version_revision INTEGER NOT NULL CHECK(version_revision > 0), observation_token TEXT NOT NULL,
            legacy_context_id TEXT NULL, state TEXT NOT NULL CHECK(state IN ('current','stale','missing')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(version_id,item_id), FOREIGN KEY(version_id,title_id) REFERENCES TitleVersions(version_id,title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(version_id,generation_id) REFERENCES VersionGenerations(version_id,generation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleItemAssociations_CurrentItem ON TitleItemAssociations(item_id) WHERE state='current';
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleItemAssociations_CurrentVersion ON TitleItemAssociations(version_id,generation_id) WHERE state='current';
        CREATE TABLE IF NOT EXISTS TitleEntityItemAssociations (
            title_id TEXT NOT NULL, item_id TEXT NOT NULL, relation_revision INTEGER NULL, title_revision INTEGER NOT NULL CHECK(title_revision > 0),
            observation_token TEXT NOT NULL, legacy_context_id TEXT NULL, state TEXT NOT NULL CHECK(state IN ('current','stale','missing')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(title_id,item_id), FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(title_id,relation_revision) REFERENCES TitleRelationSets(parent_title_id,relation_revision) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleEntityItemAssociations_CurrentTitle ON TitleEntityItemAssociations(title_id) WHERE state='current';
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleEntityItemAssociations_CurrentItem ON TitleEntityItemAssociations(item_id) WHERE state='current';
        CREATE TABLE IF NOT EXISTS TitleOperationChallenges (
            challenge_id TEXT NOT NULL PRIMARY KEY, operation_id TEXT NOT NULL, challenge_digest TEXT NOT NULL, scope_digest TEXT NOT NULL, expires_at TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('current','superseded','consumed')), predecessor_challenge_id TEXT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(predecessor_challenge_id) REFERENCES TitleOperationChallenges(challenge_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleOperationReservations (
            domain TEXT NOT NULL CHECK(domain IN ('title','version','alias','equivalence','relation')), identity_key TEXT NOT NULL,
            operation_id TEXT NOT NULL, request_digest TEXT NOT NULL, expected_head_digest TEXT NOT NULL, owner_process_instance TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(domain,identity_key), FOREIGN KEY(operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS TitleMigrationJobs (
            migration_id TEXT NOT NULL PRIMARY KEY, source_seal TEXT NOT NULL, mapping_digest TEXT NULL,
            phase TEXT NOT NULL CHECK(phase IN ('created','sealed','elected','rows-committed','projections-built','snapshot-published','activated','verified','complete','refused')),
            source_sequence INTEGER NOT NULL CHECK(source_sequence >= 0), target_sequence INTEGER NULL, snapshot_generation INTEGER NULL,
            snapshot_digest TEXT NULL, target_reader_epoch INTEGER NOT NULL CHECK(target_reader_epoch > 0), compatibility_floor INTEGER NOT NULL CHECK(compatibility_floor > 0),
            failure_code TEXT NULL, failure_detail TEXT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleMigrationJobs_Nonterminal ON TitleMigrationJobs(phase) WHERE phase NOT IN ('complete','refused');
        CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleMigrationJobs_OneActive ON TitleMigrationJobs((1)) WHERE phase NOT IN ('complete','refused');
        CREATE TABLE IF NOT EXISTS TitleMigrationElections (
            migration_id TEXT NOT NULL, class_key TEXT NOT NULL, title_id TEXT NOT NULL, member_set_digest TEXT NOT NULL,
            alias_set_digest TEXT NOT NULL, evidence_digest TEXT NOT NULL,
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(migration_id,class_key), FOREIGN KEY(migration_id) REFERENCES TitleMigrationJobs(migration_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(title_id) REFERENCES Titles(title_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE TABLE IF NOT EXISTS ContestedTitleAliases (
            migration_id TEXT NOT NULL, alias TEXT COLLATE BINARY NOT NULL, candidate_set_digest TEXT NOT NULL, reason TEXT NOT NULL,
            resolution_operation_id TEXT NULL, state TEXT NOT NULL CHECK(state IN ('contested','resolved')),
            record_version INTEGER NOT NULL CHECK(record_version = 1), created_at TEXT NOT NULL, updated_at TEXT NOT NULL, deleted_at TEXT NULL,
            PRIMARY KEY(migration_id,alias), FOREIGN KEY(migration_id) REFERENCES TitleMigrationJobs(migration_id) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(alias) REFERENCES TitleAliasClaims(alias) ON UPDATE RESTRICT ON DELETE RESTRICT,
            FOREIGN KEY(resolution_operation_id) REFERENCES TitleOperationRecords(operation_id) ON UPDATE RESTRICT ON DELETE RESTRICT);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_ContestedTitleAliases_Unresolved ON ContestedTitleAliases(alias) WHERE state='contested';
        """;

    private static readonly (string Name, string Key)[] ProtectedTables =
    [
        ("Titles", "NEW.title_id"),
        ("TitleAliasClaims", "NEW.alias"),
        ("TitleAliasResolutionEvents", "NEW.event_id"),
        ("TitleAliasHeads", "NEW.alias"),
        ("TitleAliasOrderSets", "NEW.title_id || char(0) || NEW.order_revision"),
        ("TitleAliasOrders", "NEW.title_id || char(0) || NEW.order_revision || char(0) || NEW.ordinal"),
        ("TitleAliasOrderHeads", "NEW.title_id"),
        ("LegacyAliasResponseOrders", "NEW.context_kind || char(0) || NEW.context_id || char(0) || NEW.ordinal"),
        ("TitleVersions", "NEW.version_id"),
        ("VersionGenerations", "NEW.generation_id"),
        ("GenerationComponents", "NEW.generation_id || char(0) || NEW.component_key"),
        ("GenerationEvidenceRecords", "NEW.generation_id || char(0) || NEW.evidence_role"),
        ("AcceptedTitleClaims", "NEW.claim_id"),
        ("TitleRelationSets", "NEW.parent_title_id || char(0) || NEW.relation_revision"),
        ("TitleRelations", "NEW.parent_title_id || char(0) || NEW.relation_revision || char(0) || NEW.child_title_id"),
        ("TitleRelationHeads", "NEW.parent_title_id"),
        ("TitleEquivalenceEvents", "NEW.source_title_id || char(0) || NEW.equivalence_event_id"),
        ("TitleEquivalenceHeads", "NEW.source_title_id"),
        ("TitleItemAssociations", "NEW.version_id || char(0) || NEW.item_id"),
        ("TitleEntityItemAssociations", "NEW.title_id || char(0) || NEW.item_id"),
        ("TitleOperationRecords", "NEW.operation_id"),
        ("TitleOperationPhaseEvents", "NEW.event_id"),
        ("TitleOperationChallenges", "NEW.challenge_id"),
        ("TitleOperationReservations", "NEW.domain || char(0) || NEW.identity_key"),
        ("TitleOperationDecisions", "NEW.operation_id"),
        ("TitleMigrationJobs", "NEW.migration_id"),
        ("TitleMigrationElections", "NEW.migration_id || char(0) || NEW.class_key"),
        ("ContestedTitleAliases", "NEW.migration_id || char(0) || NEW.alias")
    ];

    /// <summary>Creates the additive title schema on an already capability-registered authority connection.</summary>
    /// <param name="connection">The production authority connection with SQLite foreign keys enabled.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing schema application.</returns>
    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys";
            var enabled = Convert.ToInt64(await foreignKeys.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (enabled != 1)
            {
                throw new PermalinkException(PermalinkErrorKind.VersionRefused, "foreign-key-enforcement-disabled", "Title authority schema requires SQLite foreign keys on the connection.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InstallProtectedWritesAsync(connection, cancellationToken).ConfigureAwait(false);
        await InstallStateGuardsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refuses an unsupported persisted title record version as a typed protocol error.</summary>
    /// <param name="tableName">The record table name.</param>
    /// <param name="recordVersion">The stored version.</param>
    public static void ValidateRecordVersion(string tableName, long recordVersion)
    {
        if (recordVersion != 1)
        {
            throw new PermalinkException(PermalinkErrorKind.VersionRefused, "title-record-version-unsupported", $"Record version {recordVersion} in {tableName} is not supported.");
        }
    }

    /// <summary>Checks the frozen component-key shape before its rows are prepared.</summary>
    /// <param name="shape">The version shape.</param>
    /// <param name="componentKeys">The complete ordered component keys.</param>
    public static void ValidateComponentKeys(string shape, IReadOnlyList<string> componentKeys)
    {
        var valid = shape switch
        {
            "single" => componentKeys.Count == 1 && componentKeys[0] == "main",
            "multipart" => componentKeys.Count > 0 && IsMultipartOrder(componentKeys),
            "optical" => componentKeys.Count > 0 && IsOpticalOrder(componentKeys),
            _ => throw new PermalinkException(PermalinkErrorKind.VersionRefused, "title-version-shape-unsupported", $"Version shape {shape} is not supported.")
        };
        if (!valid)
        {
            throw new PermalinkException(PermalinkErrorKind.Conflict, "title-manifest-shape-invalid", "The component keys do not match the declared version shape.");
        }
    }

    private static bool IsMultipartOrder(IReadOnlyList<string> componentKeys)
    {
        for (var index = 0; index < componentKeys.Count; index++)
        {
            if (!string.Equals(componentKeys[index], "part:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOpticalOrder(IReadOnlyList<string> componentKeys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var path in componentKeys)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] == '/' || path.Contains('\\', StringComparison.Ordinal)
                || !seen.Add(path) || path.Split('/').Any(segment => segment is "" or "." or "..")
                || (previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0))
            {
                return false;
            }

            previous = path;
        }

        return true;
    }

    private static async Task InstallProtectedWritesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (table, newKey) in ProtectedTables)
        {
            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var key = verb == "DELETE" ? newKey.Replace("NEW.", "OLD.", StringComparison.Ordinal) : newKey;
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS protect_title_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    BEFORE {verb} ON {table}
                    WHEN (SELECT enforcement_enabled FROM AuthorityWriteProtection WHERE singleton = 1) = 1
                    BEGIN
                        SELECT CASE WHEN authority_capability(
                            '{table}', '{verb}', {key},
                            COALESCE((SELECT mac FROM AuthorityWriteAuthorizations
                                WHERE table_name = '{table}' AND verb = '{verb}' AND primary_key = {key}), '')) != 1
                            THEN RAISE(ABORT, 'permalink-protected-write') END;
                    END;
                    """;
                _ = await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var key = verb == "DELETE" ? newKey.Replace("NEW.", "OLD.", StringComparison.Ordinal) : newKey;
                await using var counter = connection.CreateCommand();
                counter.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS count_title_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    AFTER {verb} ON {table} BEGIN
                        UPDATE AuthorityCounters SET authority_sequence = authority_sequence + 1,
                            content_epoch = content_epoch + 1 WHERE singleton = 1;
                    END;
                    """;
                _ = await counter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task InstallStateGuardsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] immutable =
        [
            "TitleAliasResolutionEvents", "LegacyAliasResponseOrders", "TitleEquivalenceEvents",
            "TitleOperationPhaseEvents", "TitleOperationDecisions", "TitleMigrationElections"
        ];
        foreach (var table in immutable)
        {
            foreach (var verb in new[] { "UPDATE", "DELETE" })
            {
                await using var trigger = connection.CreateCommand();
                trigger.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS immutable_title_{table.ToLowerInvariant()}_{verb.ToLowerInvariant()}
                    BEFORE {verb} ON {table} BEGIN
                        SELECT RAISE(ABORT, 'title-immutable-record');
                    END;
                    """;
                _ = await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var guards = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title_initial_states"] = """
                CREATE TRIGGER IF NOT EXISTS title_version_initial_state
                BEFORE INSERT ON TitleVersions WHEN NEW.state != 'pending'
                BEGIN SELECT RAISE(ABORT, 'title-version-initial-state'); END;
                CREATE TRIGGER IF NOT EXISTS title_generation_initial_state
                BEFORE INSERT ON VersionGenerations WHEN NEW.state != 'prepared'
                BEGIN SELECT RAISE(ABORT, 'title-generation-initial-state'); END;
                CREATE TRIGGER IF NOT EXISTS title_challenge_initial_state
                BEFORE INSERT ON TitleOperationChallenges WHEN NEW.state != 'current'
                BEGIN SELECT RAISE(ABORT, 'title-challenge-initial-state'); END;
                CREATE TRIGGER IF NOT EXISTS title_alias_order_initial_state
                BEFORE INSERT ON TitleAliasOrderSets WHEN NEW.state != 'prepared'
                BEGIN SELECT RAISE(ABORT, 'title-alias-order-initial-state'); END;
                CREATE TRIGGER IF NOT EXISTS title_relation_set_initial_state
                BEFORE INSERT ON TitleRelationSets WHEN NEW.state != 'prepared'
                BEGIN SELECT RAISE(ABORT, 'title-relation-set-initial-state'); END;
                CREATE TRIGGER IF NOT EXISTS title_contested_initial_state
                BEFORE INSERT ON ContestedTitleAliases WHEN NEW.state != 'contested'
                BEGIN SELECT RAISE(ABORT, 'title-contested-initial-state'); END;
                """,
            ["title_operation_phase_transition"] = """
                CREATE TRIGGER IF NOT EXISTS title_operation_phase_transition
                BEFORE INSERT ON TitleOperationPhaseEvents
                WHEN NOT (
                    (NEW.predecessor_event_id IS NULL AND NEW.phase = 'prepared' AND NOT EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id)) OR
                    (NEW.phase = 'evidence-validated' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'prepared')) OR
                    (NEW.phase = 'authority-decided' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'evidence-validated')) OR
                    (NEW.phase = 'effects-applied' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'authority-decided')) OR
                    (NEW.phase = 'heads-published' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'effects-applied')) OR
                    (NEW.phase = 'projections-published' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'heads-published')) OR
                    (NEW.phase = 'complete' AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase = 'projections-published')) OR
                    (NEW.phase IN ('aborted', 'refused') AND EXISTS (SELECT 1 FROM TitleOperationPhaseEvents p WHERE p.operation_id = NEW.operation_id AND p.event_id = NEW.predecessor_event_id AND p.phase IN ('prepared', 'evidence-validated'))))
                BEGIN SELECT RAISE(ABORT, 'title-operation-phase-transition'); END;
                """,
            ["title_operation_phase_head_owner"] = """
                CREATE TRIGGER IF NOT EXISTS title_operation_phase_head_owner
                BEFORE UPDATE OF phase_event_id ON TitleOperationRecords
                WHEN NOT EXISTS (SELECT 1 FROM TitleOperationPhaseEvents e WHERE e.operation_id = NEW.operation_id AND e.event_id = NEW.phase_event_id)
                BEGIN SELECT RAISE(ABORT, 'title-operation-phase-head-owner'); END;
                """,
            ["title_generation_ready"] = """
                CREATE TRIGGER IF NOT EXISTS title_generation_ready
                BEFORE UPDATE OF state ON VersionGenerations
                WHEN NEW.state = 'ready' AND (
                    NOT EXISTS (SELECT 1 FROM GenerationEvidenceRecords e WHERE e.generation_id = NEW.generation_id AND e.evidence_role = 'content-event') OR
                    NOT EXISTS (SELECT 1 FROM GenerationEvidenceRecords e WHERE e.generation_id = NEW.generation_id AND e.evidence_role = 'path-event') OR
                    NOT EXISTS (SELECT 1 FROM GenerationEvidenceRecords e WHERE e.generation_id = NEW.generation_id AND e.evidence_role = 'binding') OR
                    NOT EXISTS (SELECT 1 FROM GenerationEvidenceRecords e WHERE e.generation_id = NEW.generation_id AND e.evidence_role = 'probe' AND e.source_record_digest = NEW.probe_result_digest) OR
                    NOT EXISTS (SELECT 1 FROM GenerationEvidenceRecords e WHERE e.generation_id = NEW.generation_id AND e.evidence_role = 'admission' AND e.operation_id = NEW.admission_operation_id) OR
                    (SELECT COUNT(*) FROM GenerationComponents c WHERE c.generation_id = NEW.generation_id) != NEW.manifest_component_count OR
                    NOT EXISTS (SELECT 1 FROM TitleOperationDecisions d JOIN TitleOperationRecords o ON o.operation_id = d.operation_id
                        WHERE d.operation_id = NEW.admission_operation_id AND d.decision = 'committed' AND o.desired_manifest_digest = NEW.manifest_digest) OR
                    NOT EXISTS (SELECT 1 FROM TitleVersions v WHERE v.version_id = NEW.version_id AND (
                        (v.shape = 'single' AND NEW.manifest_component_count = 1 AND EXISTS (SELECT 1 FROM GenerationComponents c WHERE c.generation_id = NEW.generation_id AND c.component_key = 'main' AND c.ordinal = 0)) OR
                        (v.shape = 'multipart' AND NOT EXISTS (SELECT 1 FROM GenerationComponents c WHERE c.generation_id = NEW.generation_id AND (c.component_key != 'part:' || c.ordinal OR c.ordinal >= NEW.manifest_component_count))) OR
                        (v.shape = 'optical' AND NOT EXISTS (SELECT 1 FROM GenerationComponents c WHERE c.generation_id = NEW.generation_id AND (c.tree_relative_path IS NULL OR c.component_key != c.tree_relative_path OR substr(c.tree_relative_path,1,1)='/' OR instr('/' || c.tree_relative_path || '/', '/../') > 0 OR instr(c.tree_relative_path,char(92)) > 0))))))
                BEGIN SELECT RAISE(ABORT, 'title-generation-incomplete'); END;
                """,
            ["title_generation_component_immutable"] = """
                CREATE TRIGGER IF NOT EXISTS title_generation_component_immutable_update
                BEFORE UPDATE ON GenerationComponents WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = OLD.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-component-immutable'); END;
                CREATE TRIGGER IF NOT EXISTS title_generation_component_immutable_delete
                BEFORE DELETE ON GenerationComponents WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = OLD.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-component-immutable'); END;
                CREATE TRIGGER IF NOT EXISTS title_generation_component_after_ready_insert
                BEFORE INSERT ON GenerationComponents WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = NEW.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-component-immutable'); END;
                """,
            ["title_generation_evidence_immutable"] = """
                CREATE TRIGGER IF NOT EXISTS title_generation_evidence_immutable_update
                BEFORE UPDATE ON GenerationEvidenceRecords WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = OLD.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-evidence-immutable'); END;
                CREATE TRIGGER IF NOT EXISTS title_generation_evidence_immutable_delete
                BEFORE DELETE ON GenerationEvidenceRecords WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = OLD.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-evidence-immutable'); END;
                CREATE TRIGGER IF NOT EXISTS title_generation_evidence_after_ready_insert
                BEFORE INSERT ON GenerationEvidenceRecords WHEN EXISTS (SELECT 1 FROM VersionGenerations g WHERE g.generation_id = NEW.generation_id AND g.state != 'prepared')
                BEGIN SELECT RAISE(ABORT, 'title-generation-evidence-immutable'); END;
                """,
            ["title_current_generation_ready"] = """
                CREATE TRIGGER IF NOT EXISTS title_current_generation_ready_insert
                BEFORE INSERT ON TitleVersions
                WHEN NEW.current_generation_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM VersionGenerations g WHERE g.version_id = NEW.version_id AND g.generation_id = NEW.current_generation_id AND g.state = 'ready')
                BEGIN SELECT RAISE(ABORT, 'title-current-generation-not-ready'); END;
                CREATE TRIGGER IF NOT EXISTS title_current_generation_ready
                BEFORE UPDATE OF current_generation_id ON TitleVersions
                WHEN NEW.current_generation_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM VersionGenerations g WHERE g.version_id = NEW.version_id AND g.generation_id = NEW.current_generation_id AND g.state = 'ready')
                BEGIN SELECT RAISE(ABORT, 'title-current-generation-not-ready'); END;
                """,
            ["title_current_media_generation"] = """
                CREATE TRIGGER IF NOT EXISTS title_current_media_generation_insert
                BEFORE INSERT ON TitleItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM TitleVersions v JOIN VersionGenerations g ON g.version_id = v.version_id AND g.generation_id = v.current_generation_id
                    WHERE v.version_id = NEW.version_id AND v.title_id = NEW.title_id AND v.current_generation_id = NEW.generation_id
                        AND g.state = 'ready' AND v.revision = NEW.version_revision)
                BEGIN SELECT RAISE(ABORT, 'title-current-media-generation'); END;
                CREATE TRIGGER IF NOT EXISTS title_current_media_generation_update
                BEFORE UPDATE ON TitleItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM TitleVersions v JOIN VersionGenerations g ON g.version_id = v.version_id AND g.generation_id = v.current_generation_id
                    WHERE v.version_id = NEW.version_id AND v.title_id = NEW.title_id AND v.current_generation_id = NEW.generation_id
                        AND g.state = 'ready' AND v.revision = NEW.version_revision)
                BEGIN SELECT RAISE(ABORT, 'title-current-media-generation'); END;
                """,
            ["title_current_media_kind"] = """
                CREATE TRIGGER IF NOT EXISTS title_current_media_kind
                BEFORE INSERT ON TitleItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id = NEW.title_id AND t.entity_kind IN ('movie', 'episode', 'video', 'musicvideo'))
                BEGIN SELECT RAISE(ABORT, 'title-media-association-kind'); END;
                CREATE TRIGGER IF NOT EXISTS title_current_media_kind_update
                BEFORE UPDATE ON TitleItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id = NEW.title_id AND t.entity_kind IN ('movie', 'episode', 'video', 'musicvideo'))
                BEGIN SELECT RAISE(ABORT, 'title-media-association-kind'); END;
                """,
            ["title_current_entity_kind"] = """
                CREATE TRIGGER IF NOT EXISTS title_current_entity_kind
                BEFORE INSERT ON TitleEntityItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id = NEW.title_id AND t.entity_kind IN ('series', 'season', 'boxset'))
                BEGIN SELECT RAISE(ABORT, 'title-entity-association-kind'); END;
                CREATE TRIGGER IF NOT EXISTS title_current_entity_kind_update
                BEFORE UPDATE ON TitleEntityItemAssociations WHEN NEW.state = 'current' AND NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id = NEW.title_id AND t.entity_kind IN ('series', 'season', 'boxset'))
                BEGIN SELECT RAISE(ABORT, 'title-entity-association-kind'); END;
                """,
            ["title_association_item_exclusive_media"] = """
                CREATE TRIGGER IF NOT EXISTS title_association_item_exclusive_media
                BEFORE INSERT ON TitleItemAssociations WHEN NEW.state = 'current' AND EXISTS (
                    SELECT 1 FROM TitleEntityItemAssociations a WHERE a.item_id = NEW.item_id AND a.state = 'current')
                BEGIN SELECT RAISE(ABORT, 'title-association-item-conflict'); END;
                CREATE TRIGGER IF NOT EXISTS title_association_item_exclusive_media_update
                BEFORE UPDATE ON TitleItemAssociations WHEN NEW.state = 'current' AND EXISTS (
                    SELECT 1 FROM TitleEntityItemAssociations a WHERE a.item_id = NEW.item_id AND a.state = 'current')
                BEGIN SELECT RAISE(ABORT, 'title-association-item-conflict'); END;
                """,
            ["title_association_item_exclusive_entity"] = """
                CREATE TRIGGER IF NOT EXISTS title_association_item_exclusive_entity
                BEFORE INSERT ON TitleEntityItemAssociations WHEN NEW.state = 'current' AND EXISTS (
                    SELECT 1 FROM TitleItemAssociations a WHERE a.item_id = NEW.item_id AND a.state = 'current')
                BEGIN SELECT RAISE(ABORT, 'title-association-item-conflict'); END;
                CREATE TRIGGER IF NOT EXISTS title_association_item_exclusive_entity_update
                BEFORE UPDATE ON TitleEntityItemAssociations WHEN NEW.state = 'current' AND EXISTS (
                    SELECT 1 FROM TitleItemAssociations a WHERE a.item_id = NEW.item_id AND a.state = 'current')
                BEGIN SELECT RAISE(ABORT, 'title-association-item-conflict'); END;
                """,
            ["title_alias_order_complete"] = """
                CREATE TRIGGER IF NOT EXISTS title_alias_order_complete
                BEFORE UPDATE OF state ON TitleAliasOrderSets WHEN NEW.state = 'complete' AND (
                    (SELECT COUNT(*) FROM TitleAliasOrders o WHERE o.title_id = NEW.title_id AND o.order_revision = NEW.order_revision) != NEW.alias_count OR
                    (SELECT MIN(ordinal) FROM TitleAliasOrders o WHERE o.title_id = NEW.title_id AND o.order_revision = NEW.order_revision) != 0 OR
                    (SELECT MAX(ordinal) FROM TitleAliasOrders o WHERE o.title_id = NEW.title_id AND o.order_revision = NEW.order_revision) != NEW.alias_count - 1 OR
                    NOT EXISTS (SELECT 1 FROM TitleOperationDecisions d WHERE d.operation_id = NEW.operation_id AND d.decision = 'committed'))
                BEGIN SELECT RAISE(ABORT, 'title-alias-order-incomplete'); END;
                """,
            ["title_relation_complete"] = """
                CREATE TRIGGER IF NOT EXISTS title_relation_complete
                BEFORE UPDATE OF state ON TitleRelationSets WHEN NEW.state = 'complete' AND (
                    (SELECT COUNT(*) FROM TitleRelations r WHERE r.parent_title_id = NEW.parent_title_id AND r.relation_revision = NEW.relation_revision) != NEW.child_count OR
                    NOT EXISTS (SELECT 1 FROM TitleOperationDecisions d WHERE d.operation_id = NEW.operation_id AND d.decision = 'committed'))
                BEGIN SELECT RAISE(ABORT, 'title-relation-set-incomplete'); END;
                """,
            ["title_relation_coordinates"] = """
                CREATE TRIGGER IF NOT EXISTS title_relation_coordinates
                BEFORE INSERT ON TitleRelations WHEN NEW.relation_kind = 'episode' AND EXISTS (
                    SELECT 1 FROM TitleRelations r WHERE r.parent_title_id = NEW.parent_title_id AND r.relation_revision = NEW.relation_revision
                    AND r.relation_kind = 'episode' AND r.season_number = NEW.season_number AND r.episode_number = NEW.episode_number)
                BEGIN SELECT RAISE(ABORT, 'title-episode-coordinate-conflict'); END;
                """,
            ["title_alias_head_committed"] = """
                CREATE TRIGGER IF NOT EXISTS title_alias_head_committed
                BEFORE INSERT ON TitleAliasHeads WHEN NOT EXISTS (
                    SELECT 1 FROM TitleAliasResolutionEvents e JOIN TitleOperationDecisions d ON d.operation_id = e.operation_id
                    WHERE e.alias = NEW.alias AND e.event_id = NEW.event_id AND e.predecessor_event_id IS NULL AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-alias-head-uncommitted'); END;
                """,
            ["title_equivalence_predecessor_scope"] = """
                CREATE TRIGGER IF NOT EXISTS title_equivalence_predecessor_scope
                BEFORE INSERT ON TitleEquivalenceEvents WHEN NEW.predecessor_event_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM TitleEquivalenceEvents p WHERE p.source_title_id = NEW.source_title_id AND p.equivalence_event_id = NEW.predecessor_event_id)
                BEGIN SELECT RAISE(ABORT, 'title-equivalence-predecessor-scope'); END;
                """,
            ["title_equivalence_same_kind"] = """
                CREATE TRIGGER IF NOT EXISTS title_equivalence_same_kind
                BEFORE INSERT ON TitleEquivalenceEvents WHEN NOT EXISTS (
                    SELECT 1 FROM Titles s JOIN Titles t ON t.title_id = NEW.target_title_id
                    WHERE s.title_id = NEW.source_title_id AND s.entity_kind = t.entity_kind)
                BEGIN SELECT RAISE(ABORT, 'title-equivalence-kind-conflict'); END;
                """,
            ["title_equivalence_no_cycle"] = """
                CREATE TRIGGER IF NOT EXISTS title_equivalence_no_cycle
                BEFORE INSERT ON TitleEquivalenceHeads WHEN EXISTS (
                    WITH RECURSIVE reachable(title_id) AS (
                        SELECT e.target_title_id FROM TitleEquivalenceEvents e WHERE e.source_title_id = NEW.source_title_id
                            AND e.equivalence_event_id = NEW.equivalence_event_id AND e.state = 'merged'
                        UNION
                        SELECT e.target_title_id FROM reachable r
                        JOIN TitleEquivalenceHeads h ON h.source_title_id = r.title_id
                        JOIN TitleEquivalenceEvents e ON e.source_title_id = h.source_title_id
                            AND e.equivalence_event_id = h.equivalence_event_id
                        WHERE e.state = 'merged')
                    SELECT 1 FROM reachable WHERE title_id = NEW.source_title_id)
                BEGIN SELECT RAISE(ABORT, 'title-equivalence-cycle'); END;
                """,
            ["title_accepted_claim_decision"] = """
                CREATE TRIGGER IF NOT EXISTS title_accepted_claim_decision
                BEFORE INSERT ON AcceptedTitleClaims WHEN NEW.state = 'accepted' AND NOT EXISTS (
                    SELECT 1 FROM TitleOperationDecisions d WHERE d.operation_id = NEW.acceptance_operation_id AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-claim-uncommitted'); END;
                """,
            ["title_alias_resolution_decision"] = """
                CREATE TRIGGER IF NOT EXISTS title_alias_resolution_decision
                BEFORE INSERT ON TitleAliasResolutionEvents WHEN NEW.state = 'resolved' AND NOT EXISTS (
                    SELECT 1 FROM TitleOperationDecisions d WHERE d.operation_id = NEW.operation_id AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-alias-resolution-uncommitted'); END;
                """,
            ["title_alias_order_head_complete"] = """
                CREATE TRIGGER IF NOT EXISTS title_alias_order_head_complete
                BEFORE INSERT ON TitleAliasOrderHeads WHEN NOT EXISTS (
                    SELECT 1 FROM TitleAliasOrderSets s JOIN TitleOperationDecisions d ON d.operation_id = s.operation_id
                    WHERE s.title_id = NEW.title_id AND s.order_revision = NEW.order_revision AND s.state = 'complete' AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-alias-order-head-incomplete'); END;
                """,
            ["title_relation_head_complete"] = """
                CREATE TRIGGER IF NOT EXISTS title_relation_head_complete
                BEFORE INSERT ON TitleRelationHeads WHEN NOT EXISTS (
                    SELECT 1 FROM TitleRelationSets s JOIN TitleOperationDecisions d ON d.operation_id = s.operation_id
                    WHERE s.parent_title_id = NEW.parent_title_id AND s.relation_revision = NEW.relation_revision AND s.state = 'complete' AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-relation-head-incomplete'); END;
                """,
            ["title_equivalence_head_decision"] = """
                CREATE TRIGGER IF NOT EXISTS title_equivalence_head_decision
                BEFORE INSERT ON TitleEquivalenceHeads WHEN NOT EXISTS (
                    SELECT 1 FROM TitleEquivalenceEvents e JOIN TitleOperationDecisions d ON d.operation_id = e.operation_id
                    WHERE e.source_title_id = NEW.source_title_id AND e.equivalence_event_id = NEW.equivalence_event_id
                        AND e.predecessor_event_id IS NULL AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-equivalence-head-uncommitted'); END;
                """,
            ["title_relation_kind_scope"] = """
                CREATE TRIGGER IF NOT EXISTS title_relation_kind_scope
                BEFORE INSERT ON TitleRelations WHEN NOT EXISTS (
                    SELECT 1 FROM Titles p JOIN Titles c ON c.title_id = NEW.child_title_id
                    WHERE p.title_id = NEW.parent_title_id AND (
                        (NEW.relation_kind = 'season' AND p.entity_kind = 'series' AND c.entity_kind = 'season') OR
                        (NEW.relation_kind = 'episode' AND p.entity_kind IN ('series','season') AND c.entity_kind = 'episode') OR
                        (NEW.relation_kind = 'collection-member' AND p.entity_kind = 'boxset')))
                BEGIN SELECT RAISE(ABORT, 'title-relation-kind-scope'); END;
                """,
            ["title_relation_parent_kind"] = """
                CREATE TRIGGER IF NOT EXISTS title_relation_parent_kind
                BEFORE INSERT ON TitleRelationSets WHEN NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id=NEW.parent_title_id AND t.entity_kind IN ('series','season','boxset'))
                BEGIN SELECT RAISE(ABORT, 'title-relation-parent-kind'); END;
                """,
            ["title_accepted_claim_scope"] = """
                CREATE TRIGGER IF NOT EXISTS title_accepted_claim_scope
                BEFORE INSERT ON AcceptedTitleClaims WHEN NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id=NEW.title_id AND t.entity_kind=NEW.entity_kind)
                BEGIN SELECT RAISE(ABORT, 'title-claim-kind-mismatch'); END;
                """,
            ["title_season_coordinate_conflict"] = """
                CREATE TRIGGER IF NOT EXISTS title_season_coordinate_conflict
                BEFORE INSERT ON TitleRelations WHEN NEW.relation_kind='season' AND EXISTS (
                    SELECT 1 FROM TitleRelations r WHERE r.parent_title_id=NEW.parent_title_id AND r.relation_revision=NEW.relation_revision
                        AND r.relation_kind='season' AND r.season_number=NEW.season_number)
                BEGIN SELECT RAISE(ABORT, 'title-season-coordinate-conflict'); END;
                """,
            ["title_alias_order_target"] = """
                CREATE TRIGGER IF NOT EXISTS title_alias_order_target
                BEFORE INSERT ON TitleAliasOrders WHEN NOT EXISTS (
                    SELECT 1 FROM TitleAliasHeads h JOIN TitleAliasResolutionEvents r ON r.alias = h.alias AND r.event_id = h.event_id
                    WHERE h.alias = NEW.alias AND r.state = 'resolved' AND (
                        r.target_title_id = NEW.title_id OR EXISTS (
                            WITH RECURSIVE alias_chain(title_id) AS (
                                SELECT r.target_title_id UNION
                                SELECT e.target_title_id FROM alias_chain a JOIN TitleEquivalenceHeads eh ON eh.source_title_id = a.title_id
                                JOIN TitleEquivalenceEvents e ON e.source_title_id = eh.source_title_id AND e.equivalence_event_id = eh.equivalence_event_id
                                WHERE e.state = 'merged')
                            SELECT 1 FROM alias_chain WHERE title_id = NEW.title_id)))
                BEGIN SELECT RAISE(ABORT, 'title-alias-order-target-mismatch'); END;
                """,
            ["title_legacy_alias_order"] = """
                CREATE TRIGGER IF NOT EXISTS title_legacy_alias_order_insert
                BEFORE INSERT ON LegacyAliasResponseOrders WHEN (NEW.ordinal = 0 AND NEW.was_canonical != 1) OR (NEW.ordinal > 0 AND (NEW.was_canonical != 0 OR NOT EXISTS (
                    SELECT 1 FROM LegacyAliasResponseOrders r WHERE r.context_kind = NEW.context_kind AND r.context_id = NEW.context_id AND r.ordinal = 0 AND r.was_canonical = 1)))
                BEGIN SELECT RAISE(ABORT, 'title-legacy-alias-order-canonical'); END;
                """,
            ["title_migration_election_unresolved"] = """
                CREATE TRIGGER IF NOT EXISTS title_migration_election_unresolved
                BEFORE INSERT ON TitleMigrationElections WHEN NOT EXISTS (
                    SELECT 1 FROM Titles t WHERE t.title_id = NEW.title_id AND t.state = 'unresolved')
                BEGIN SELECT RAISE(ABORT, 'title-migration-election-not-unresolved'); END;
                """,
            ["title_contested_resolution_decision"] = """
                CREATE TRIGGER IF NOT EXISTS title_contested_resolution_decision
                BEFORE UPDATE OF state ON ContestedTitleAliases WHEN NEW.state = 'resolved' AND NOT EXISTS (
                    SELECT 1 FROM TitleOperationDecisions d WHERE d.operation_id = NEW.resolution_operation_id AND d.decision = 'committed')
                BEGIN SELECT RAISE(ABORT, 'title-contested-resolution-uncommitted'); END;
                """,
            ["title_challenge_current_unique"] = """
                CREATE UNIQUE INDEX IF NOT EXISTS UX_TitleOperationChallenges_Current ON TitleOperationChallenges(operation_id) WHERE state = 'current';
                """
        };

        guards["title_alias_claim_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_alias_claim_immutable_update
            BEFORE UPDATE ON TitleAliasClaims WHEN OLD.state = 'issued'
            BEGIN SELECT RAISE(ABORT, 'title-alias-claim-immutable'); END;
            CREATE TRIGGER IF NOT EXISTS title_alias_claim_immutable_delete
            BEFORE DELETE ON TitleAliasClaims WHEN OLD.state = 'issued'
            BEGIN SELECT RAISE(ABORT, 'title-alias-claim-immutable'); END;
            """;
        guards["title_alias_order_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_alias_order_immutable_insert
            BEFORE INSERT ON TitleAliasOrders WHEN EXISTS (SELECT 1 FROM TitleAliasOrderSets s WHERE s.title_id = NEW.title_id AND s.order_revision = NEW.order_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-alias-order-immutable'); END;
            CREATE TRIGGER IF NOT EXISTS title_alias_order_immutable_update
            BEFORE UPDATE ON TitleAliasOrders WHEN EXISTS (SELECT 1 FROM TitleAliasOrderSets s WHERE s.title_id = OLD.title_id AND s.order_revision = OLD.order_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-alias-order-immutable'); END;
            CREATE TRIGGER IF NOT EXISTS title_alias_order_immutable_delete
            BEFORE DELETE ON TitleAliasOrders WHEN EXISTS (SELECT 1 FROM TitleAliasOrderSets s WHERE s.title_id = OLD.title_id AND s.order_revision = OLD.order_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-alias-order-immutable'); END;
            """;
        guards["title_relation_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_relation_immutable_insert
            BEFORE INSERT ON TitleRelations WHEN EXISTS (SELECT 1 FROM TitleRelationSets s WHERE s.parent_title_id = NEW.parent_title_id AND s.relation_revision = NEW.relation_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-relation-immutable'); END;
            CREATE TRIGGER IF NOT EXISTS title_relation_immutable_update
            BEFORE UPDATE ON TitleRelations WHEN EXISTS (SELECT 1 FROM TitleRelationSets s WHERE s.parent_title_id = OLD.parent_title_id AND s.relation_revision = OLD.relation_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-relation-immutable'); END;
            CREATE TRIGGER IF NOT EXISTS title_relation_immutable_delete
            BEFORE DELETE ON TitleRelations WHEN EXISTS (SELECT 1 FROM TitleRelationSets s WHERE s.parent_title_id = OLD.parent_title_id AND s.relation_revision = OLD.relation_revision AND s.state = 'complete')
            BEGIN SELECT RAISE(ABORT, 'title-relation-immutable'); END;
            """;
        guards["title_operation_request_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_operation_request_immutable
            BEFORE UPDATE OF command_kind,request_version,request_json,request_digest,actor_id,capability_scope_digest,expected_heads_json,desired_sets_json,desired_manifest_digest ON TitleOperationRecords
            BEGIN SELECT RAISE(ABORT, 'title-operation-request-immutable'); END;
            """;
        guards["title_migration_transition"] = """
            CREATE TRIGGER IF NOT EXISTS title_migration_initial_phase
            BEFORE INSERT ON TitleMigrationJobs WHEN NEW.phase != 'created'
            BEGIN SELECT RAISE(ABORT, 'title-migration-initial-phase'); END;
            CREATE TRIGGER IF NOT EXISTS title_migration_transition
            BEFORE UPDATE OF phase ON TitleMigrationJobs
            WHEN NOT ((OLD.phase='created' AND NEW.phase IN ('sealed','refused')) OR (OLD.phase='sealed' AND NEW.phase IN ('elected','refused')) OR
                (OLD.phase='elected' AND NEW.phase IN ('rows-committed','refused')) OR (OLD.phase='rows-committed' AND NEW.phase IN ('projections-built','refused')) OR
                (OLD.phase='projections-built' AND NEW.phase IN ('snapshot-published','refused')) OR (OLD.phase='snapshot-published' AND NEW.phase IN ('activated','refused')) OR
                (OLD.phase='activated' AND NEW.phase='verified') OR (OLD.phase='verified' AND NEW.phase='complete'))
            BEGIN SELECT RAISE(ABORT, 'title-migration-phase-transition'); END;
            """;
        guards["title_reservation_terminal_release"] = """
            CREATE TRIGGER IF NOT EXISTS title_reservation_terminal_release
            BEFORE DELETE ON TitleOperationReservations WHEN NOT EXISTS (
                SELECT 1 FROM TitleOperationRecords r JOIN TitleOperationPhaseEvents e ON e.operation_id = r.operation_id AND e.event_id = r.phase_event_id
                WHERE r.operation_id = OLD.operation_id AND e.phase IN ('complete','aborted','refused'))
            BEGIN SELECT RAISE(ABORT, 'title-reservation-not-terminal'); END;
            """;
        guards["title_operation_decision_scope"] = """
            CREATE TRIGGER IF NOT EXISTS title_operation_decision_scope
            BEFORE INSERT ON TitleOperationDecisions WHEN NOT EXISTS (
                SELECT 1 FROM TitleOperationRecords r JOIN TitleOperationPhaseEvents e ON e.operation_id = r.operation_id
                WHERE r.operation_id = NEW.operation_id AND r.request_digest = NEW.request_digest
                    AND e.event_id = NEW.journal_event_id AND e.phase = CASE NEW.decision WHEN 'committed' THEN 'authority-decided' WHEN 'refused' THEN 'refused' ELSE 'aborted' END)
            BEGIN SELECT RAISE(ABORT, 'title-operation-decision-scope'); END;
            """;
        guards["title_alias_head_cas"] = """
            CREATE TRIGGER IF NOT EXISTS title_alias_head_cas_update
            BEFORE UPDATE ON TitleAliasHeads WHEN NEW.revision != OLD.revision + 1 OR NOT EXISTS (
                SELECT 1 FROM TitleAliasResolutionEvents e JOIN TitleOperationDecisions d ON d.operation_id = e.operation_id
                WHERE e.alias = NEW.alias AND e.event_id = NEW.event_id AND e.predecessor_event_id = OLD.event_id AND d.decision = 'committed')
            BEGIN SELECT RAISE(ABORT, 'title-alias-head-cas'); END;
            """;
        guards["title_alias_order_head_cas"] = """
            CREATE TRIGGER IF NOT EXISTS title_alias_order_head_cas_update
            BEFORE UPDATE ON TitleAliasOrderHeads WHEN NEW.order_revision != OLD.order_revision + 1 OR NOT EXISTS (
                SELECT 1 FROM TitleAliasOrderSets s JOIN TitleOperationDecisions d ON d.operation_id = s.operation_id
                WHERE s.title_id = NEW.title_id AND s.order_revision = NEW.order_revision AND s.state = 'complete' AND s.operation_id = NEW.operation_id AND d.decision = 'committed')
            BEGIN SELECT RAISE(ABORT, 'title-alias-order-head-cas'); END;
            """;
        guards["title_relation_head_cas"] = """
            CREATE TRIGGER IF NOT EXISTS title_relation_head_cas_update
            BEFORE UPDATE ON TitleRelationHeads WHEN NEW.relation_revision != OLD.relation_revision + 1 OR NOT EXISTS (
                SELECT 1 FROM TitleRelationSets s JOIN TitleOperationDecisions d ON d.operation_id = s.operation_id
                WHERE s.parent_title_id = NEW.parent_title_id AND s.relation_revision = NEW.relation_revision AND s.state = 'complete' AND s.operation_id = NEW.operation_id AND d.decision = 'committed')
            BEGIN SELECT RAISE(ABORT, 'title-relation-head-cas'); END;
            """;
        guards["title_equivalence_head_cas"] = """
            CREATE TRIGGER IF NOT EXISTS title_equivalence_head_cas
            BEFORE UPDATE ON TitleEquivalenceHeads WHEN NEW.revision != OLD.revision + 1 OR NOT EXISTS (
                SELECT 1 FROM TitleEquivalenceEvents e JOIN TitleOperationDecisions d ON d.operation_id = e.operation_id
                WHERE e.source_title_id = NEW.source_title_id AND e.equivalence_event_id = NEW.equivalence_event_id
                    AND e.predecessor_event_id = OLD.equivalence_event_id AND d.decision = 'committed')
            BEGIN SELECT RAISE(ABORT, 'title-equivalence-head-cas'); END;
            """;
        guards["title_challenge_owner"] = """
            CREATE TRIGGER IF NOT EXISTS title_challenge_predecessor_owner
            BEFORE INSERT ON TitleOperationChallenges WHEN NEW.predecessor_challenge_id IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM TitleOperationChallenges p WHERE p.challenge_id = NEW.predecessor_challenge_id AND p.operation_id = NEW.operation_id)
            BEGIN SELECT RAISE(ABORT, 'title-challenge-predecessor-owner'); END;
            """;
        guards["title_equivalence_no_cycle"] = """
            CREATE TRIGGER IF NOT EXISTS title_equivalence_no_cycle_update
            BEFORE UPDATE ON TitleEquivalenceHeads WHEN EXISTS (
                WITH RECURSIVE reachable(title_id) AS (
                    SELECT e.target_title_id FROM TitleEquivalenceEvents e WHERE e.source_title_id = NEW.source_title_id
                        AND e.equivalence_event_id = NEW.equivalence_event_id AND e.state = 'merged'
                    UNION
                    SELECT e.target_title_id FROM reachable r JOIN TitleEquivalenceHeads h ON h.source_title_id = r.title_id
                    JOIN TitleEquivalenceEvents e ON e.source_title_id = h.source_title_id AND e.equivalence_event_id = h.equivalence_event_id
                    WHERE e.state = 'merged') SELECT 1 FROM reachable WHERE title_id = NEW.source_title_id)
            BEGIN SELECT RAISE(ABORT, 'title-equivalence-cycle'); END;
            """;
        guards["title_title_identity_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_identity_immutable
            BEFORE UPDATE ON Titles WHEN NEW.title_id != OLD.title_id OR NEW.entity_kind != OLD.entity_kind OR NEW.revision != OLD.revision + 1
            BEGIN SELECT RAISE(ABORT, 'title-identity-revision'); END;
            """;
        guards["title_version_membership_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_version_membership_immutable
            BEFORE UPDATE ON TitleVersions WHEN NEW.version_id != OLD.version_id OR NEW.title_id != OLD.title_id OR NEW.shape != OLD.shape OR NEW.revision != OLD.revision + 1
            BEGIN SELECT RAISE(ABORT, 'title-version-membership-immutable'); END;
            """;
        guards["title_generation_state_transition"] = """
            CREATE TRIGGER IF NOT EXISTS title_generation_state_transition
            BEFORE UPDATE OF state ON VersionGenerations
            WHEN NOT ((OLD.state='prepared' AND NEW.state IN ('ready','quarantined')) OR (OLD.state='ready' AND NEW.state IN ('superseded','quarantined')) OR OLD.state=NEW.state)
            BEGIN SELECT RAISE(ABORT, 'title-generation-state-transition'); END;
            """;
        guards["title_alias_order_set_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_alias_order_set_immutable
            BEFORE UPDATE ON TitleAliasOrderSets WHEN OLD.state='complete' OR NEW.title_id != OLD.title_id OR NEW.order_revision != OLD.order_revision OR NEW.alias_count != OLD.alias_count OR NEW.set_digest != OLD.set_digest OR NEW.operation_id != OLD.operation_id
            BEGIN SELECT RAISE(ABORT, 'title-alias-order-set-immutable'); END;
            """;
        guards["title_relation_set_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_relation_set_immutable
            BEFORE UPDATE ON TitleRelationSets WHEN OLD.state='complete' OR NEW.parent_title_id != OLD.parent_title_id OR NEW.relation_revision != OLD.relation_revision OR NEW.child_count != OLD.child_count OR NEW.set_digest != OLD.set_digest OR NEW.operation_id != OLD.operation_id
            BEGIN SELECT RAISE(ABORT, 'title-relation-set-immutable'); END;
            """;
        guards["title_entity_current_relation"] = """
            CREATE TRIGGER IF NOT EXISTS title_entity_current_relation_insert
            BEFORE INSERT ON TitleEntityItemAssociations WHEN NEW.state='current' AND EXISTS (
                SELECT 1 FROM Titles t WHERE t.title_id=NEW.title_id AND t.entity_kind IN ('series','season','boxset'))
                AND (NEW.relation_revision IS NULL OR NOT EXISTS (
                SELECT 1 FROM Titles t JOIN TitleRelationHeads h ON h.parent_title_id=t.title_id
                JOIN TitleRelationSets s ON s.parent_title_id=h.parent_title_id AND s.relation_revision=h.relation_revision
                WHERE t.title_id=NEW.title_id AND t.entity_kind IN ('series','season','boxset') AND t.revision=NEW.title_revision
                    AND h.relation_revision=NEW.relation_revision AND s.state='complete'))
            BEGIN SELECT RAISE(ABORT, 'title-entity-association-relation'); END;
            CREATE TRIGGER IF NOT EXISTS title_entity_current_relation_update
            BEFORE UPDATE ON TitleEntityItemAssociations WHEN NEW.state='current' AND EXISTS (
                SELECT 1 FROM Titles t WHERE t.title_id=NEW.title_id AND t.entity_kind IN ('series','season','boxset'))
                AND (NEW.relation_revision IS NULL OR NOT EXISTS (
                SELECT 1 FROM Titles t JOIN TitleRelationHeads h ON h.parent_title_id=t.title_id
                JOIN TitleRelationSets s ON s.parent_title_id=h.parent_title_id AND s.relation_revision=h.relation_revision
                WHERE t.title_id=NEW.title_id AND t.entity_kind IN ('series','season','boxset') AND t.revision=NEW.title_revision
                    AND h.relation_revision=NEW.relation_revision AND s.state='complete'))
            BEGIN SELECT RAISE(ABORT, 'title-entity-association-relation'); END;
            """;
        guards["title_media_association_title_revision"] = """
            CREATE TRIGGER IF NOT EXISTS title_media_association_title_revision
            BEFORE INSERT ON TitleItemAssociations WHEN NEW.state='current' AND NOT EXISTS (
                SELECT 1 FROM Titles t WHERE t.title_id=NEW.title_id AND t.revision=NEW.title_revision)
            BEGIN SELECT RAISE(ABORT, 'title-media-association-title-revision'); END;
            CREATE TRIGGER IF NOT EXISTS title_media_association_title_revision_update
            BEFORE UPDATE ON TitleItemAssociations WHEN NEW.state='current' AND NOT EXISTS (
                SELECT 1 FROM Titles t WHERE t.title_id=NEW.title_id AND t.revision=NEW.title_revision)
            BEGIN SELECT RAISE(ABORT, 'title-media-association-title-revision'); END;
            """;
        guards["title_operation_phase_head_cas"] = """
            CREATE TRIGGER IF NOT EXISTS title_operation_phase_head_cas
            BEFORE UPDATE OF phase_event_id ON TitleOperationRecords WHEN NOT EXISTS (
                SELECT 1 FROM TitleOperationPhaseEvents e WHERE e.operation_id=NEW.operation_id AND e.event_id=NEW.phase_event_id
                    AND e.predecessor_event_id=OLD.phase_event_id)
            BEGIN SELECT RAISE(ABORT, 'title-operation-phase-head-cas'); END;
            """;
        guards["title_operation_challenge_transition"] = """
            CREATE TRIGGER IF NOT EXISTS title_operation_challenge_transition
            BEFORE UPDATE ON TitleOperationChallenges WHEN NEW.challenge_id != OLD.challenge_id OR NEW.operation_id != OLD.operation_id
                OR NEW.challenge_digest != OLD.challenge_digest OR NEW.scope_digest != OLD.scope_digest OR NEW.expires_at != OLD.expires_at
                OR NEW.predecessor_challenge_id IS NOT OLD.predecessor_challenge_id
                OR NOT ((OLD.state='current' AND NEW.state IN ('superseded','consumed')) OR OLD.state=NEW.state)
            BEGIN SELECT RAISE(ABORT, 'title-challenge-state-transition'); END;
            """;
        guards["title_accepted_claim_transition"] = """
            CREATE TRIGGER IF NOT EXISTS title_accepted_claim_transition
            BEFORE UPDATE ON AcceptedTitleClaims WHEN NEW.claim_id != OLD.claim_id OR NEW.title_id != OLD.title_id OR NEW.namespace != OLD.namespace
                OR NEW.entity_kind != OLD.entity_kind OR NEW.scope_key != OLD.scope_key OR NEW.value != OLD.value
                OR NEW.parent_title_id IS NOT OLD.parent_title_id OR NEW.provenance != OLD.provenance
                OR NEW.acceptance_operation_id != OLD.acceptance_operation_id OR NEW.accepted_title_revision != OLD.accepted_title_revision
                OR NOT (OLD.state='accepted' AND NEW.state IN ('revoked','contested'))
            BEGIN SELECT RAISE(ABORT, 'title-claim-state-transition'); END;
            """;
        guards["title_generation_evidence_fields_immutable"] = """
            CREATE TRIGGER IF NOT EXISTS title_generation_fields_immutable
            BEFORE UPDATE ON VersionGenerations WHEN NEW.generation_id != OLD.generation_id OR NEW.version_id != OLD.version_id
                OR NEW.generation_ordinal != OLD.generation_ordinal OR NEW.capsule_id != OLD.capsule_id OR NEW.content_event_id != OLD.content_event_id
                OR NEW.path_event_id != OLD.path_event_id OR NEW.manifest_digest != OLD.manifest_digest
                OR NEW.manifest_component_count != OLD.manifest_component_count OR NEW.probe_result_digest != OLD.probe_result_digest
                OR NEW.admission_operation_id != OLD.admission_operation_id
            BEGIN SELECT RAISE(ABORT, 'title-generation-evidence-immutable'); END;
            """;

        foreach (var statement in guards.Values)
        {
            await using var guard = connection.CreateCommand();
            guard.CommandText = statement;
            _ = await guard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
