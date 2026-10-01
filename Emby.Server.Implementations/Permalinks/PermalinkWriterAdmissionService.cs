// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Persists fail-closed, single-use external writer admissions.</summary>
internal sealed class PermalinkWriterAdmissionService : IPermalinkWriterAdmissionService
{
    private const int SqliteUniqueConstraintExtendedCode = 2067;
    private static readonly TimeSpan StartLease = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatLease = TimeSpan.FromSeconds(15);
    private static readonly string[] AllowedActions = ["replace-media", "replace-path"];
    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;

    public PermalinkWriterAdmissionService(PermalinkAuthorityStore authority, TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> IssueAsync(
        PermalinkWriterAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return GuardStoreAsync(() => IssueCoreAsync(request, cancellationToken), cancellationToken);
    }

    private async Task<PermalinkWriterAdmission> IssueCoreAsync(
        PermalinkWriterAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var admissionId = Guid.NewGuid();
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (contentEpoch, latestGeneration) = await ReadFreshnessAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (request.SnapshotGeneration <= 0 || latestGeneration <= 0 || request.SnapshotGeneration != latestGeneration)
        {
            throw Conflict("writer-admission-stale-snapshot", "The requested snapshot is not the latest published generation.");
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            admissionId,
            "PermalinkWriterAdmissions",
            "INSERT",
            admissionId.ToString("D"),
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO PermalinkWriterAdmissions
                (admission_id, root_identity_id, normalized_relative_path,
                 pinned_object_identity, action, snapshot_generation,
                 authority_epoch, content_epoch, status, start_deadline_utc,
                 heartbeat_deadline_utc, created_at, updated_at)
            SELECT $id, root_identity_id, $path, $object, $action, $generation,
                   $epoch, $content, 'issued', $deadline, NULL, $now, $now
              FROM PermalinkRootIdentities WHERE root_identity_id = $root
            """;
            command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
            command.Parameters.AddWithValue("$root", request.RootIdentityId);
            command.Parameters.AddWithValue("$path", request.NormalizedRelativePath);
            command.Parameters.AddWithValue("$object", request.PinnedObjectIdentity);
            command.Parameters.AddWithValue("$action", request.Action);
            command.Parameters.AddWithValue("$generation", request.SnapshotGeneration);
            command.Parameters.AddWithValue("$epoch", PermalinkAuthoritySchema.SupportedProtocolEpoch);
            command.Parameters.AddWithValue("$content", contentEpoch);
            command.Parameters.AddWithValue("$deadline", Format(now + StartLease));
            command.Parameters.AddWithValue("$now", Format(now));
            try
            {
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw Conflict("writer-admission-unknown-root", "The requested root identity is not registered.");
                }
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteUniqueConstraintExtendedCode)
            {
                throw Conflict("writer-admission-path-reserved", "The canonical path already has an active writer reservation.", exception);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PermalinkWriterAdmission(
            admissionId,
            "issued",
            now + StartLease,
            null,
            PermalinkAuthoritySchema.SupportedProtocolEpoch,
            contentEpoch,
            true);
    }

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> StartAsync(Guid admissionId, CancellationToken cancellationToken)
        => TransitionAsync(admissionId, "start", cancellationToken);

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> HeartbeatAsync(Guid admissionId, CancellationToken cancellationToken)
        => TransitionAsync(admissionId, "heartbeat", cancellationToken);

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> PollAsync(Guid admissionId, CancellationToken cancellationToken)
        => TransitionAsync(admissionId, "poll", cancellationToken);

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> CompleteAsync(Guid admissionId, CancellationToken cancellationToken)
        => TransitionAsync(admissionId, "complete", cancellationToken);

    private Task<PermalinkWriterAdmission> TransitionAsync(
        Guid admissionId,
        string operation,
        CancellationToken cancellationToken)
        => GuardStoreAsync(() => TransitionCoreAsync(admissionId, operation, cancellationToken), cancellationToken);

    /// <summary>Converts any store or IO failure into a typed fail-closed error that keeps its cause.</summary>
    private static async Task<PermalinkWriterAdmission> GuardStoreAsync(
        Func<Task<PermalinkWriterAdmission>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "writer-admission-store-failure",
                $"The writer admission store failed closed ({exception.GetType().Name}: {exception.Message}).",
                exception);
        }
    }

    private async Task<PermalinkWriterAdmission> TransitionCoreAsync(
        Guid admissionId,
        string operation,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadAsync(connection, transaction, admissionId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("writer-admission-missing", "The writer admission does not exist.");
        var expired = row.Status == "issued" ? now > row.StartDeadlineUtc :
            row.Status == "started" && (row.HeartbeatDeadlineUtc is null || now > row.HeartbeatDeadlineUtc);
        if (expired)
        {
            row = row with { Status = "recovery-required", Allowed = false };
            await UpdateAsync(_authority, connection, transaction, row, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }

        row = operation switch
        {
            "start" when row.Status == "issued" => row with
            {
                Status = "started",
                HeartbeatDeadlineUtc = now + HeartbeatLease,
                Allowed = true
            },
            "heartbeat" when row.Status == "started" => row with
            {
                HeartbeatDeadlineUtc = now + HeartbeatLease,
                Allowed = true
            },
            "poll" when row.Status == "started" => row with { Allowed = true },
            "complete" when row.Status == "started" => row with { Status = "completed", Allowed = false },
            _ => row with { Allowed = false }
        };
        if (operation is "start" or "heartbeat" or "complete")
        {
            await UpdateAsync(_authority, connection, transaction, row, now, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private static async Task<(long ContentEpoch, long LatestGeneration)> ReadFreshnessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT content_epoch, COALESCE((SELECT MAX(generation) FROM SnapshotMetadata), 0)
              FROM AuthorityCounters WHERE singleton = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("authority counter row is missing");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<PermalinkWriterAdmission?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid admissionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, start_deadline_utc, heartbeat_deadline_utc,
                   authority_epoch, content_epoch
              FROM PermalinkWriterAdmissions WHERE admission_id = $id
            """;
        command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PermalinkWriterAdmission(
            admissionId,
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(3),
            reader.GetInt64(4),
            false);
    }

    private static async Task UpdateAsync(
        PermalinkAuthorityStore authority,
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkWriterAdmission admission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = await authority.BeginAuthorizedWriteAsync(
            connection,
            admission.AdmissionId,
            "PermalinkWriterAdmissions",
            "UPDATE",
            admission.AdmissionId.ToString("D"),
            cancellationToken,
            transaction).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE PermalinkWriterAdmissions
               SET status = $status, heartbeat_deadline_utc = $heartbeat, updated_at = $now
             WHERE admission_id = $id
            """;
        command.Parameters.AddWithValue("$status", admission.Status);
        command.Parameters.AddWithValue("$heartbeat", admission.HeartbeatDeadlineUtc is null ? DBNull.Value : Format(admission.HeartbeatDeadlineUtc.Value));
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$id", admission.AdmissionId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw Conflict("writer-admission-raced", "The writer admission changed concurrently.");
        }
    }

    private static void ValidateRequest(PermalinkWriterAdmissionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RootIdentityId)
            || string.IsNullOrWhiteSpace(request.PinnedObjectIdentity)
            || !AllowedActions.Contains(request.Action, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(request.NormalizedRelativePath)
            || Path.IsPathRooted(request.NormalizedRelativePath)
            || request.NormalizedRelativePath.Contains('\\', StringComparison.Ordinal)
            || request.NormalizedRelativePath.StartsWith('/')
            || request.NormalizedRelativePath.Split('/').Any(segment => segment is "." or ".." or ""))
        {
            throw Conflict("writer-admission-invalid", "The writer admission request is incomplete or not canonical-relative.");
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static PermalinkException Conflict(string code, string message, Exception? exception = null)
        => new(PermalinkErrorKind.Conflict, code, message, exception);
}
