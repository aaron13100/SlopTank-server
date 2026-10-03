// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-02, 2026-10-03.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Data access for the per-key path state a lease records and rechecks: anchor-intent rows in
/// <c>PermalinkEvidenceRegistry</c>, <c>PathReservations</c>, <c>PendingSupersessionSlots</c>
/// and <c>PermalinkWriterAdmissions</c>. It reads and writes rows and makes no decisions; the
/// lease decides which result is a refusal.
/// </summary>
internal sealed class PermalinkPathEvidenceStore
{
    /// <summary>The registry <c>evidence_kind</c> of an anchor-intent row.</summary>
    public const string AnchorIntentKind = "anchor-intent";

    /// <summary>The intent state while the anchor may exist on disk without an Ensure commit.</summary>
    public const string IntentPending = "pending";

    /// <summary>The intent state after Ensure's transaction committed.</summary>
    public const string IntentCommitted = "committed";

    /// <summary>The intent state after an unsettled lease removed its own anchor.</summary>
    public const string IntentAbandoned = "abandoned";

    /// <summary>The registry <c>evidence_kind</c> of a new item deferred by the scanner.</summary>
    public const string PendingScanKind = "pending-scan";

    /// <summary>The pending-scan state while the candidate has not been indexed.</summary>
    public const string ScanPending = "pending";

    /// <summary>The pending-scan state after a later scan indexed the candidate.</summary>
    public const string ScanIndexed = "indexed";

    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;

    public PermalinkPathEvidenceStore(PermalinkAuthorityStore authority, TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>Reads the authority-wide content epoch.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The content epoch.</returns>
    public async Task<long> ReadContentEpochAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync(
            connection,
            null,
            "SELECT content_epoch FROM AuthorityCounters WHERE singleton = 1",
            Array.Empty<(string, object)>(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the highest reservation generation ever recorded for a key, 0 when none.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction, when reading inside one.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest generation.</returns>
    public Task<long> ReadMaxReservationGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string rootIdentityId,
        string relativePath,
        CancellationToken cancellationToken)
        => ScalarAsync(
            connection,
            transaction,
            "SELECT COALESCE(MAX(generation), 0) FROM PathReservations WHERE root_identity_id = $root AND normalized_relative_path = $path",
            KeyParameters(rootIdentityId, relativePath),
            cancellationToken);

    /// <summary>Counts active reservations on a key.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count.</returns>
    public Task<long> CountActiveReservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string relativePath,
        CancellationToken cancellationToken)
        => ScalarAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM PathReservations WHERE root_identity_id = $root AND normalized_relative_path = $path AND status = 'active'",
            KeyParameters(rootIdentityId, relativePath),
            cancellationToken);

    /// <summary>Counts pending supersession slots on a key.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count.</returns>
    public Task<long> CountPendingSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string relativePath,
        CancellationToken cancellationToken)
        => ScalarAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM PendingSupersessionSlots WHERE root_identity_id = $root AND normalized_relative_path = $path AND status = 'pending'",
            KeyParameters(rootIdentityId, relativePath),
            cancellationToken);

    /// <summary>Counts writer admissions on a key that still block it (issued, started or recovery-required).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="excludedAdmissionId">The current external writer's own row, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count.</returns>
    public Task<long> CountBlockingAdmissionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string relativePath,
        Guid? excludedAdmissionId,
        CancellationToken cancellationToken)
        => ScalarAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM PermalinkWriterAdmissions WHERE root_identity_id = $root AND normalized_relative_path = $path AND status IN ('issued', 'started', 'recovery-required') AND ($excluded IS NULL OR admission_id <> $excluded)",
            [
                ("$root", rootIdentityId),
                ("$path", relativePath),
                ("$excluded", excludedAdmissionId?.ToString("D") ?? (object)DBNull.Value)
            ],
            cancellationToken);

    /// <summary>Reads the exact writer admission offered as the current writer's capability.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="admissionId">The offered admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bound evidence, or null when the identifier is not bound to this key.</returns>
    public async Task<PermalinkWriterAdmissionEvidence?> ReadWriterAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootIdentityId,
        string relativePath,
        Guid admissionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pinned_object_identity, action, authority_epoch, status, heartbeat_deadline_utc
              FROM PermalinkWriterAdmissions
             WHERE admission_id = $id AND root_identity_id = $root
               AND normalized_relative_path = $path
            """;
        command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
        command.Parameters.AddWithValue("$root", rootIdentityId);
        command.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PermalinkWriterAdmissionEvidence(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>Records a pending anchor intent, committed on its own before any xattr write.</summary>
    /// <param name="registryId">The registry row id.</param>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="relativePath">The normalized relative path.</param>
    /// <param name="anchorToken">The token about to be written as the xattr; recovery compares against it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task InsertAnchorIntentAsync(
        Guid registryId,
        string rootIdentityId,
        string relativePath,
        string anchorToken,
        CancellationToken cancellationToken)
    {
        var key = registryId.ToString("D");
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            registryId,
            "PermalinkEvidenceRegistry",
            "INSERT",
            key,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PermalinkEvidenceRegistry
                (registry_id, evidence_kind, root_identity_id, normalized_relative_path, operation_id, state, created_at, updated_at, anchor_token)
            VALUES ($id, $kind, $root, $path, NULL, $state, $now, $now, $token)
            """;
        command.Parameters.AddWithValue("$id", key);
        command.Parameters.AddWithValue("$kind", AnchorIntentKind);
        command.Parameters.AddWithValue("$root", rootIdentityId);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$state", IntentPending);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$token", anchorToken);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an anchor-intent row to a new state inside a caller-owned transaction.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction that owns the write.</param>
    /// <param name="registryId">The registry row id.</param>
    /// <param name="state">The new state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SetIntentStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid registryId,
        string state,
        CancellationToken cancellationToken)
    {
        var key = registryId.ToString("D");
        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            registryId,
            "PermalinkEvidenceRegistry",
            "UPDATE",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE PermalinkEvidenceRegistry SET state = $state, updated_at = $now WHERE registry_id = $id";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", key);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an anchor-intent row to a new state in its own transaction.</summary>
    /// <param name="registryId">The registry row id.</param>
    /// <param name="state">The new state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SetIntentStateAsync(Guid registryId, string state, CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetIntentStateAsync(connection, transaction, registryId, state, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one reserved new-item scan candidate, idempotently per canonical key.</summary>
    /// <param name="key">The candidate's canonical key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the durable registry write.</returns>
    public async Task RecordPendingScanResultAsync(
        PermalinkLeasedKey key,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = """
                SELECT registry_id FROM PermalinkEvidenceRegistry
                 WHERE evidence_kind = $kind AND root_identity_id = $root
                   AND normalized_relative_path = $path AND state = $state
                 LIMIT 1
                """;
            existing.Parameters.AddWithValue("$kind", PendingScanKind);
            existing.Parameters.AddWithValue("$root", key.RootIdentityId);
            existing.Parameters.AddWithValue("$path", key.NormalizedRelativePath);
            existing.Parameters.AddWithValue("$state", ScanPending);
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
        }

        var id = Guid.NewGuid();
        var idText = id.ToString("D");
        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            id,
            "PermalinkEvidenceRegistry",
            "INSERT",
            idText,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PermalinkEvidenceRegistry
                (registry_id, evidence_kind, root_identity_id, normalized_relative_path, operation_id, state, created_at, updated_at, anchor_token)
            VALUES ($id, $kind, $root, $path, NULL, $state, $now, $now, NULL)
            """;
        command.Parameters.AddWithValue("$id", idText);
        command.Parameters.AddWithValue("$kind", PendingScanKind);
        command.Parameters.AddWithValue("$root", key.RootIdentityId);
        command.Parameters.AddWithValue("$path", key.NormalizedRelativePath);
        command.Parameters.AddWithValue("$state", ScanPending);
        command.Parameters.AddWithValue("$now", Now());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks every pending scan result for a successfully indexed key as indexed.</summary>
    /// <param name="key">The indexed key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the durable registry update.</returns>
    public async Task CompletePendingScanResultsAsync(
        PermalinkLeasedKey key,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<Guid>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT registry_id FROM PermalinkEvidenceRegistry
                 WHERE evidence_kind = $kind AND root_identity_id = $root
                   AND normalized_relative_path = $path AND state = $state
                 ORDER BY created_at, registry_id
                """;
            select.Parameters.AddWithValue("$kind", PendingScanKind);
            select.Parameters.AddWithValue("$root", key.RootIdentityId);
            select.Parameters.AddWithValue("$path", key.NormalizedRelativePath);
            select.Parameters.AddWithValue("$state", ScanPending);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        foreach (var id in ids)
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await SetIntentStateAsync(
                connection,
                transaction,
                id,
                ScanIndexed,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns whether any pending-scan registry evidence still needs reconciliation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when at least one pending row exists.</returns>
    public async Task<bool> HasPendingScanResultsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM PermalinkEvidenceRegistry
                 WHERE evidence_kind = $kind AND state = $state)
            """;
        command.Parameters.AddWithValue("$kind", PendingScanKind);
        command.Parameters.AddWithValue("$state", ScanPending);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Reads the relative paths that currently have pending scanner evidence.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The distinct NFC-normalized relative paths.</returns>
    public async Task<IReadOnlySet<string>> ReadPendingScanRelativePathsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT normalized_relative_path FROM PermalinkEvidenceRegistry
             WHERE evidence_kind = $kind AND state = $state
            """;
        command.Parameters.AddWithValue("$kind", PendingScanKind);
        command.Parameters.AddWithValue("$state", ScanPending);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>Reads the current state of an anchor-intent row.</summary>
    /// <param name="registryId">The registry row id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or null when the row does not exist.</returns>
    public async Task<string?> ReadIntentStateAsync(Guid registryId, CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM PermalinkEvidenceRegistry WHERE registry_id = $id";
        command.Parameters.AddWithValue("$id", registryId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    /// <summary>Lists every anchor-intent row still pending, oldest first.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pending rows; a row's token is null when it predates the token column.</returns>
    public async Task<IReadOnlyList<PermalinkAnchorIntentRow>> ListPendingAnchorIntentsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT registry_id, root_identity_id, normalized_relative_path, anchor_token
              FROM PermalinkEvidenceRegistry
             WHERE evidence_kind = $kind AND state = $state
             ORDER BY created_at, registry_id
            """;
        command.Parameters.AddWithValue("$kind", AnchorIntentKind);
        command.Parameters.AddWithValue("$state", IntentPending);
        var rows = new List<PermalinkAnchorIntentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PermalinkAnchorIntentRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private static (string Name, object Value)[] KeyParameters(string rootIdentityId, string relativePath)
        => new (string, object)[] { ("$root", rootIdentityId), ("$path", relativePath) };

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private string Now() => _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
