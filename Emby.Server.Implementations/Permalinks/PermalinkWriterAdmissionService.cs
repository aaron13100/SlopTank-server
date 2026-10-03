// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-04.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    private readonly PermalinkContentRoots? _contentRoots;
    private readonly PermalinkEvidenceRoots? _evidenceRoots;
    private readonly PermalinkRootIdentityRegistry? _roots;
    private readonly IPermalinkPathObjects? _objects;

    /// <summary>Initializes a new instance of the <see cref="PermalinkWriterAdmissionService"/> class.</summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="timeProvider">The time provider.</param>
    public PermalinkWriterAdmissionService(PermalinkAuthorityStore authority, TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>Initializes a new instance of the <see cref="PermalinkWriterAdmissionService"/> class.</summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="contentRoots">The configured content roots.</param>
    /// <param name="evidenceRoots">The separately configured evidence roots.</param>
    /// <param name="roots">The root identity registry.</param>
    /// <param name="objects">The no-follow physical object inspector.</param>
    public PermalinkWriterAdmissionService(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider,
        PermalinkContentRoots contentRoots,
        PermalinkEvidenceRoots evidenceRoots,
        PermalinkRootIdentityRegistry roots,
        IPermalinkPathObjects objects)
        : this(authority, timeProvider)
    {
        _contentRoots = contentRoots;
        _evidenceRoots = evidenceRoots;
        _roots = roots;
        _objects = objects;
    }

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> IssueRetirementAsync(
        PermalinkRetirementAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRetirementRequest(request);
        return GuardStoreAsync(() => IssueRetirementCoreAsync(request, cancellationToken), cancellationToken);
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

    private async Task<PermalinkWriterAdmission> IssueRetirementCoreAsync(
        PermalinkRetirementAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var existing = await ReadRetirementAsync(
            connection,
            transaction: null,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSameRetirement(existing, request);
            return WithCurrentAllowance(existing.Admission, now);
        }

        var binding = await ResolveRetirementBindingAsync(request, cancellationToken).ConfigureAwait(false);
        now = _timeProvider.GetUtcNow();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        existing = await ReadRetirementAsync(
            connection,
            transaction,
            request.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSameRetirement(existing, request);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return WithCurrentAllowance(existing.Admission, now);
        }

        var (_, latestGeneration) = await ReadFreshnessAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (request.SnapshotGeneration <= 0
            || latestGeneration <= 0
            || request.SnapshotGeneration != latestGeneration)
        {
            throw Conflict("writer-admission-stale-snapshot", "The requested snapshot is not the latest published generation.");
        }

        var key = request.OperationId.ToString("D");
        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            request.OperationId,
            "PermalinkEvidenceRegistry",
            "INSERT",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PermalinkEvidenceRegistry
                    (registry_id, evidence_kind, root_identity_id, normalized_relative_path,
                     operation_id, state, created_at, updated_at, anchor_token)
                VALUES ($id, 'retirement-copy', $root, $path, $id, 'pending', $now, $now, NULL)
                """;
            insert.Parameters.AddWithValue("$id", key);
            insert.Parameters.AddWithValue("$root", binding.DestinationRoot.RootIdentityId);
            insert.Parameters.AddWithValue("$path", request.DestinationNormalizedRelativePath);
            insert.Parameters.AddWithValue("$now", Format(now));
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var contentEpoch = await ReadContentEpochAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            request.OperationId,
            "PermalinkWriterAdmissions",
            "INSERT",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PermalinkWriterAdmissions
                    (admission_id, root_identity_id, normalized_relative_path,
                     pinned_object_identity, action, snapshot_generation,
                     authority_epoch, content_epoch, status, start_deadline_utc,
                     heartbeat_deadline_utc, created_at, updated_at)
                VALUES ($id, $root, $path, $object, 'retire-copy', $generation,
                        $epoch, $content, 'issued', $deadline, NULL, $now, $now)
                """;
            insert.Parameters.AddWithValue("$id", key);
            insert.Parameters.AddWithValue("$root", binding.SourceRoot.RootIdentityId);
            insert.Parameters.AddWithValue("$path", request.SourceNormalizedRelativePath);
            insert.Parameters.AddWithValue("$object", request.SourcePinnedObjectIdentity);
            insert.Parameters.AddWithValue("$generation", request.SnapshotGeneration);
            insert.Parameters.AddWithValue("$epoch", PermalinkAuthoritySchema.SupportedProtocolEpoch);
            insert.Parameters.AddWithValue("$content", contentEpoch);
            insert.Parameters.AddWithValue("$deadline", Format(now + StartLease));
            insert.Parameters.AddWithValue("$now", Format(now));
            try
            {
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteUniqueConstraintExtendedCode)
            {
                throw Conflict(
                    "writer-admission-path-reserved",
                    "The retirement source already has an active writer reservation.",
                    exception);
            }
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            request.OperationId,
            "PermalinkRetirementIntents",
            "INSERT",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PermalinkRetirementIntents
                    (operation_id, registry_id, admission_id, destination_root_identity_id,
                     destination_normalized_relative_path, destination_parent_pinned_object_identity,
                     snapshot_generation, published_object_identity, created_at, updated_at)
                VALUES ($id, $id, $id, $root, $path, $parent, $generation, NULL, $now, $now)
                """;
            insert.Parameters.AddWithValue("$id", key);
            insert.Parameters.AddWithValue("$root", binding.DestinationRoot.RootIdentityId);
            insert.Parameters.AddWithValue("$path", request.DestinationNormalizedRelativePath);
            insert.Parameters.AddWithValue("$parent", request.DestinationParentPinnedObjectIdentity);
            insert.Parameters.AddWithValue("$generation", request.SnapshotGeneration);
            insert.Parameters.AddWithValue("$now", Format(now));
            try
            {
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteUniqueConstraintExtendedCode)
            {
                throw Conflict(
                    "retirement-destination-reserved",
                    "The retirement destination was already registered by another operation.",
                    exception);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermalinkWriterAdmission(
            request.OperationId,
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

    /// <inheritdoc />
    public Task<PermalinkWriterAdmission> SettleRetirementAsync(Guid admissionId, CancellationToken cancellationToken)
        => GuardStoreAsync(() => SettleRetirementCoreAsync(admissionId, cancellationToken), cancellationToken);

    private async Task<PermalinkWriterAdmission> SettleRetirementCoreAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadRetirementAsync(connection, transaction: null, admissionId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("retirement-intent-missing", "The retirement intent does not exist.");
        if (existing.Admission.Status == "completed" && existing.RegistryState == "committed")
        {
            return existing.Admission with { Allowed = false };
        }

        var now = _timeProvider.GetUtcNow();
        if (existing.Admission.Status != "started"
            || existing.Admission.HeartbeatDeadlineUtc is null
            || now > existing.Admission.HeartbeatDeadlineUtc)
        {
            throw Conflict("retirement-admission-not-active", "The retirement admission is not actively leased.");
        }

        var request = new PermalinkRetirementAdmissionRequest(
            admissionId,
            existing.SourceRootIdentityId,
            existing.SourceRelativePath,
            existing.SourcePinnedObjectIdentity,
            existing.DestinationRootIdentityId,
            existing.DestinationRelativePath,
            existing.DestinationParentPinnedObjectIdentity,
            existing.BindingSnapshotGeneration);
        var binding = await ResolveRetirementBindingAsync(request, cancellationToken, requireDestinationAbsent: false).ConfigureAwait(false);
        var destinationPath = ComposePath(binding.DestinationRoot.CanonicalRoot, request.DestinationNormalizedRelativePath);
        var published = _objects!.ReadPinnedObjectNoFollowExact(destinationPath)
            ?? throw Conflict("retirement-destination-missing", "The retirement destination has not been published.");
        if (published.IsSymbolicLink)
        {
            throw Conflict("retirement-destination-symlink", "The retirement destination is a symbolic link.");
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadRetirementAsync(connection, transaction, admissionId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("retirement-intent-missing", "The retirement intent does not exist.");
        now = _timeProvider.GetUtcNow();
        if (current.Admission.Status != "started"
            || current.Admission.HeartbeatDeadlineUtc is null
            || now > current.Admission.HeartbeatDeadlineUtc
            || current.RegistryState != "pending")
        {
            throw Conflict("retirement-admission-not-active", "The retirement admission changed before settlement.");
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection, admissionId, "PermalinkRetirementIntents", "UPDATE", admissionId.ToString("D"), cancellationToken, transaction).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE PermalinkRetirementIntents SET published_object_identity = $object, updated_at = $now WHERE operation_id = $id AND published_object_identity IS NULL";
            command.Parameters.AddWithValue("$object", published.Identity);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("retirement-settlement-raced", "The retirement intent changed during settlement.");
            }
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection, admissionId, "PermalinkEvidenceRegistry", "UPDATE", admissionId.ToString("D"), cancellationToken, transaction).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE PermalinkEvidenceRegistry SET state = 'committed', updated_at = $now WHERE registry_id = $id AND evidence_kind = 'retirement-copy' AND state = 'pending'";
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("retirement-settlement-raced", "The retirement registry row changed during settlement.");
            }
        }

        var completed = current.Admission with { Status = "completed", Allowed = false };
        await UpdateAsync(_authority, connection, transaction, completed, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

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

        if (operation == "complete"
            && await IsRetirementAsync(connection, transaction, admissionId, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "retirement-settlement-required",
                "A retirement admission can complete only through typed publication settlement.");
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

    private static async Task<long> ReadContentEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_epoch FROM AuthorityCounters WHERE singleton = 1";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidDataException("authority counter row is missing")
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsRetirementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid admissionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM PermalinkRetirementIntents WHERE admission_id = $id";
        command.Parameters.AddWithValue("$id", admissionId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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

    private static void ValidateRetirementRequest(PermalinkRetirementAdmissionRequest request)
    {
        if (request.OperationId.Equals(Guid.Empty)
            || string.IsNullOrWhiteSpace(request.SourceRootIdentityId)
            || string.IsNullOrWhiteSpace(request.SourcePinnedObjectIdentity)
            || string.IsNullOrWhiteSpace(request.DestinationRootIdentityId)
            || string.IsNullOrWhiteSpace(request.DestinationParentPinnedObjectIdentity)
            || !IsCanonicalRelative(request.SourceNormalizedRelativePath)
            || !IsCanonicalRelative(request.DestinationNormalizedRelativePath))
        {
            throw Conflict(
                "retirement-admission-invalid",
                "The retirement admission request is incomplete or not canonical-relative.");
        }
    }

    private static bool IsCanonicalRelative(string path)
        => !string.IsNullOrWhiteSpace(path)
            && !Path.IsPathRooted(path)
            && !path.Contains('\\', StringComparison.Ordinal)
            && !path.StartsWith('/')
            && path.Normalize(NormalizationForm.FormC) == path
            && path.Split('/').All(segment => segment is not "." and not ".." and not "");

    private async Task<RetirementBinding> ResolveRetirementBindingAsync(
        PermalinkRetirementAdmissionRequest request,
        CancellationToken cancellationToken,
        bool requireDestinationAbsent = true)
    {
        if (_contentRoots is null || _evidenceRoots is null || _roots is null || _objects is null)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "retirement-admission-unavailable",
                "The retirement admission service has no configured path inspectors.");
        }

        var sourceRoot = await ResolveRoleRootAsync(
            _contentRoots.ConfiguredRoots,
            request.SourceRootIdentityId,
            "retirement-source-root-role",
            cancellationToken).ConfigureAwait(false);
        var destinationRoot = await ResolveRoleRootAsync(
            _evidenceRoots.ConfiguredRoots,
            request.DestinationRootIdentityId,
            "retirement-destination-root-role",
            cancellationToken).ConfigureAwait(false);
        var sourcePath = ComposePath(sourceRoot.CanonicalRoot, request.SourceNormalizedRelativePath);
        var destinationPath = ComposePath(destinationRoot.CanonicalRoot, request.DestinationNormalizedRelativePath);
        var sourceParentPath = Path.GetDirectoryName(sourcePath)
            ?? throw Conflict("retirement-source-unavailable", "The retirement source has no parent.");
        var canonicalSourceParent = _objects.CanonicalizeDirectory(sourceParentPath)
            ?? throw Conflict("retirement-source-unavailable", "The retirement source parent is absent or unavailable.");
        if (!string.Equals(canonicalSourceParent, Path.GetFullPath(sourceParentPath), StringComparison.Ordinal))
        {
            throw Conflict("retirement-source-symlink", "The retirement source traverses a symbolic link.");
        }

        var source = _objects.ReadPinnedObjectNoFollowExact(sourcePath)
            ?? throw Conflict("retirement-source-unavailable", "The retirement source is absent.");
        if (source.IsSymbolicLink)
        {
            throw Conflict("retirement-source-symlink", "The retirement source is a symbolic link.");
        }

        if (!string.Equals(source.Identity, request.SourcePinnedObjectIdentity, StringComparison.Ordinal))
        {
            throw Conflict("retirement-source-changed", "The retirement source object identity changed.");
        }

        var parentPath = Path.GetDirectoryName(destinationPath)
            ?? throw Conflict("retirement-parent-unavailable", "The retirement destination has no parent.");
        var canonicalParent = _objects.CanonicalizeDirectory(parentPath)
            ?? throw Conflict("retirement-parent-unavailable", "The retirement destination parent is absent or unavailable.");
        if (!string.Equals(canonicalParent, Path.GetFullPath(parentPath), StringComparison.Ordinal))
        {
            throw Conflict("retirement-parent-symlink", "The retirement destination parent traverses a symbolic link.");
        }

        var parent = _objects.ReadPinnedObjectNoFollowExact(parentPath)
            ?? throw Conflict("retirement-parent-unavailable", "The retirement destination parent is absent.");
        if (parent.IsSymbolicLink)
        {
            throw Conflict("retirement-parent-symlink", "The retirement destination parent is a symbolic link.");
        }

        if (!string.Equals(parent.Identity, request.DestinationParentPinnedObjectIdentity, StringComparison.Ordinal))
        {
            throw Conflict("retirement-parent-changed", "The retirement destination parent object identity changed.");
        }

        if (requireDestinationAbsent && _objects.ReadPinnedObjectNoFollowExact(destinationPath) is not null)
        {
            throw Conflict("retirement-destination-present", "The retirement destination is not absent.");
        }

        return new RetirementBinding(sourceRoot, destinationRoot);
    }

    private async Task<PermalinkRootIdentity> ResolveRoleRootAsync(
        IReadOnlyList<string> configuredRoots,
        string requestedIdentity,
        string code,
        CancellationToken cancellationToken)
    {
        foreach (var configuredRoot in configuredRoots)
        {
            var identity = await _roots!.EnsureKnownAsync(configuredRoot, cancellationToken).ConfigureAwait(false);
            if (string.Equals(identity.RootIdentityId, requestedIdentity, StringComparison.Ordinal))
            {
                return identity;
            }
        }

        throw Conflict(code, $"Root identity '{requestedIdentity}' is not configured for the requested role.");
    }

    private static string ComposePath(string canonicalRoot, string relativePath)
    {
        var path = Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!string.Equals(PermalinkPathKey.Normalize(canonicalRoot, path), relativePath, StringComparison.Ordinal))
        {
            throw Conflict("retirement-admission-invalid", "The retirement path is not canonical for its root.");
        }

        return path;
    }

    private static async Task<RetirementExisting?> ReadRetirementAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.root_identity_id, a.normalized_relative_path, a.pinned_object_identity,
                   a.snapshot_generation, a.status, a.start_deadline_utc,
                   a.heartbeat_deadline_utc, a.authority_epoch, a.content_epoch,
                   r.evidence_kind, r.root_identity_id, r.normalized_relative_path,
                   r.operation_id, r.state,
                   i.destination_parent_pinned_object_identity, i.snapshot_generation,
                   i.published_object_identity
              FROM PermalinkRetirementIntents i
              JOIN PermalinkWriterAdmissions a ON a.admission_id = i.admission_id
              JOIN PermalinkEvidenceRegistry r ON r.registry_id = i.registry_id
             WHERE i.operation_id = $id
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset? heartbeat = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new RetirementExisting(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetInt64(15),
            new PermalinkWriterAdmission(
                operationId,
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                heartbeat,
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(4) == "issued"),
            await reader.IsDBNullAsync(16, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(16));
    }

    private static void RequireSameRetirement(
        RetirementExisting existing,
        PermalinkRetirementAdmissionRequest request)
    {
        if (!string.Equals(existing.SourceRootIdentityId, request.SourceRootIdentityId, StringComparison.Ordinal)
            || !string.Equals(existing.SourceRelativePath, request.SourceNormalizedRelativePath, StringComparison.Ordinal)
            || !string.Equals(existing.SourcePinnedObjectIdentity, request.SourcePinnedObjectIdentity, StringComparison.Ordinal)
            || existing.AdmissionSnapshotGeneration != request.SnapshotGeneration
            || existing.EvidenceKind != "retirement-copy"
            || !string.Equals(existing.DestinationRootIdentityId, request.DestinationRootIdentityId, StringComparison.Ordinal)
            || !string.Equals(existing.DestinationRelativePath, request.DestinationNormalizedRelativePath, StringComparison.Ordinal)
            || !string.Equals(existing.RegistryOperationId, request.OperationId.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(existing.DestinationParentPinnedObjectIdentity, request.DestinationParentPinnedObjectIdentity, StringComparison.Ordinal)
            || existing.BindingSnapshotGeneration != request.SnapshotGeneration)
        {
            throw Conflict(
                "retirement-operation-conflict",
                "The retirement operation id is already bound to different evidence.");
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static PermalinkWriterAdmission WithCurrentAllowance(
        PermalinkWriterAdmission admission,
        DateTimeOffset now)
        => admission with
        {
            Allowed = admission.Status switch
            {
                "issued" => now <= admission.StartDeadlineUtc,
                "started" => admission.HeartbeatDeadlineUtc is not null && now <= admission.HeartbeatDeadlineUtc,
                _ => false
            }
        };

    private static PermalinkException Conflict(string code, string message, Exception? exception = null)
        => new(PermalinkErrorKind.Conflict, code, message, exception);

    private sealed record RetirementBinding(
        PermalinkRootIdentity SourceRoot,
        PermalinkRootIdentity DestinationRoot);

    private sealed record RetirementExisting(
        string SourceRootIdentityId,
        string SourceRelativePath,
        string SourcePinnedObjectIdentity,
        long AdmissionSnapshotGeneration,
        string EvidenceKind,
        string DestinationRootIdentityId,
        string DestinationRelativePath,
        string RegistryOperationId,
        string RegistryState,
        string DestinationParentPinnedObjectIdentity,
        long BindingSnapshotGeneration,
        PermalinkWriterAdmission Admission,
        string? PublishedObjectIdentity);
}
