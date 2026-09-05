using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns the authority SQLite schema and atomic genesis claim set.
/// </summary>
internal static class PermalinkAuthoritySchema
{
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
            -- FirstAliasClaims is keyed by capsule_id, so every lookup BY ALIAS
            -- was a full scan: the resolver's own binding join does one, and so
            -- does the competing-claims test in BindAsync, which runs for every
            -- alias of every ensure. Without this the repeat-ensure caching test
            -- fails outright on this host.
            CREATE INDEX IF NOT EXISTS IX_FirstAliasClaims_PermalinkId
                ON FirstAliasClaims(permalink_id);
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
