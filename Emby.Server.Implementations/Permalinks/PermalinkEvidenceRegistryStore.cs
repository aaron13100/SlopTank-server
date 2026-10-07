// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Persists versioned population plans, compare-and-swap cursors, and append-preserving evidence
/// supersession. Filesystem traversal stays outside this data-access component.
/// </summary>
internal sealed class PermalinkEvidenceRegistryStore
{
    private const int CursorVersion = 1;
    private const int WriterVersion = 1;
    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="PermalinkEvidenceRegistryStore"/> class.</summary>
    /// <param name="authority">The authority database.</param>
    /// <param name="timeProvider">The source of durable timestamps.</param>
    public PermalinkEvidenceRegistryStore(PermalinkAuthorityStore authority, TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>Names one typed source traversed by a resumable registry population.</summary>
    internal enum PermalinkPopulationSourceKind
    {
        EvidenceRoot,
        JournalHot,
        JournalArchive,
        AuthorityIntent
    }

    /// <summary>Names one typed recoverable evidence class persisted by population.</summary>
    internal enum PermalinkPopulationEvidenceKind
    {
        EvidenceFile,
        JournalStaging,
        JournalQuarantine,
        RetirementCopy,
        MutationIntent
    }

    /// <summary>Creates or resumes one exact population plan without replacing its source generations.</summary>
    /// <param name="populationId">The stable population identifier.</param>
    /// <param name="sources">The complete typed source plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task BeginAsync(
        string populationId,
        IReadOnlyCollection<PermalinkPopulationSource> sources,
        CancellationToken cancellationToken)
    {
        RequireText(populationId, nameof(populationId));
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("A registry population requires at least one source.", nameof(sources));
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            Validate(source);
            if (!sourceKeys.Add(SourceKind(source.Kind) + "\0" + source.Key))
            {
                throw new ArgumentException("A registry population source may appear only once.", nameof(sources));
            }
        }

        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = await ReadPopulationAsync(connection, transaction, populationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            var operationId = StableGuid("population", populationId);
            using (var scope = await _authority.BeginAuthorizedWriteAsync(
                connection,
                operationId,
                "PermalinkRegistryPopulations",
                "INSERT",
                populationId,
                cancellationToken,
                transaction).ConfigureAwait(false))
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO PermalinkRegistryPopulations
                        (population_id, registry_writer_version, status, completed_content_epoch,
                         registry_cardinality, created_at, updated_at)
                    VALUES ($id, $version, 'running', NULL, NULL, $now, $now)
                    """;
                insert.Parameters.AddWithValue("$id", populationId);
                insert.Parameters.AddWithValue("$version", WriterVersion);
                insert.Parameters.AddWithValue("$now", Now());
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (existing.WriterVersion != WriterVersion)
        {
            throw VersionRefused(existing.WriterVersion);
        }
        else if (!string.Equals(existing.Status, "running", StringComparison.Ordinal))
        {
            throw Conflict("registry-population-not-running", $"Registry population '{populationId}' is not running.");
        }
        else if (!await MatchesPlanAsync(connection, transaction, populationId, sources, cancellationToken)
                     .ConfigureAwait(false))
        {
            throw Conflict(
                "registry-population-plan-changed",
                $"Registry population '{populationId}' cannot resume with a different source plan.");
        }

        if (existing is null)
        {
            foreach (var source in sources)
            {
                await InsertCheckpointAsync(connection, transaction, populationId, source, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one durable source cursor after a process restart.</summary>
    /// <param name="populationId">The stable population identifier.</param>
    /// <param name="source">The exact typed source identity and generation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The checkpoint, or <see langword="null"/> when the source is absent.</returns>
    public async Task<PermalinkPopulationCheckpoint?> ReadCheckpointAsync(
        string populationId,
        PermalinkPopulationSource source,
        CancellationToken cancellationToken)
    {
        RequireText(populationId, nameof(populationId));
        Validate(source);
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_generation, cursor_version, cursor, observed_count, status
              FROM PermalinkRegistryPopulationCheckpoints
             WHERE population_id = $population AND source_kind = $kind AND source_key = $key
               AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$population", populationId);
        command.Parameters.AddWithValue("$kind", SourceKind(source.Kind));
        command.Parameters.AddWithValue("$key", source.Key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        RequireGeneration(source, reader.GetString(0));
        return new PermalinkPopulationCheckpoint(
            reader.GetInt32(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetInt64(3),
            string.Equals(reader.GetString(4), "complete", StringComparison.Ordinal));
    }

    /// <summary>Advances one source cursor exactly once; a stale resumer is refused.</summary>
    /// <param name="populationId">The stable population identifier.</param>
    /// <param name="source">The exact typed source identity and generation.</param>
    /// <param name="expectedCursor">The cursor read before doing the batch.</param>
    /// <param name="nextCursor">The cursor after the completed batch.</param>
    /// <param name="observedCount">The cumulative evidence count.</param>
    /// <param name="complete">Whether this source is fully traversed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task AdvanceCheckpointAsync(
        string populationId,
        PermalinkPopulationSource source,
        string? expectedCursor,
        string nextCursor,
        long observedCount,
        bool complete,
        CancellationToken cancellationToken)
    {
        RequireText(populationId, nameof(populationId));
        Validate(source);
        RequireText(nextCursor, nameof(nextCursor));
        if (observedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedCount));
        }

        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var key = CheckpointKey(populationId, source);
        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            StableGuid("checkpoint", key),
            "PermalinkRegistryPopulationCheckpoints",
            "UPDATE",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE PermalinkRegistryPopulationCheckpoints
                   SET cursor = $next, observed_count = $count,
                       status = $status, updated_at = $now
                 WHERE population_id = $population AND source_kind = $kind AND source_key = $key
                   AND source_generation = $generation AND cursor_version = $version
                   AND (($expected IS NULL AND cursor IS NULL) OR cursor = $expected)
                   AND status IN ('pending', 'running') AND deleted_at IS NULL
                """;
            update.Parameters.AddWithValue("$next", nextCursor);
            update.Parameters.AddWithValue("$count", observedCount);
            update.Parameters.AddWithValue("$status", complete ? "complete" : "running");
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$population", populationId);
            update.Parameters.AddWithValue("$kind", SourceKind(source.Kind));
            update.Parameters.AddWithValue("$key", source.Key);
            update.Parameters.AddWithValue("$generation", source.Generation);
            update.Parameters.AddWithValue("$version", CursorVersion);
            update.Parameters.AddWithValue("$expected", (object?)expectedCursor ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict(
                    "registry-population-checkpoint-raced",
                    $"Population source '{source.Key}' no longer has expected cursor '{expectedCursor ?? "<start>"}'.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers one observation idempotently and retains the replaced row as evidence.</summary>
    /// <param name="populationId">The running population identifier.</param>
    /// <param name="evidence">The validated physical or logical observation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The active registry identifier and whether a new row was created.</returns>
    public async Task<PermalinkPopulationEvidenceResult> RecordEvidenceAsync(
        string populationId,
        PermalinkPopulationEvidence evidence,
        CancellationToken cancellationToken)
    {
        RequireText(populationId, nameof(populationId));
        Validate(evidence);
        var normalizedEvidence = evidence with { ContentSha256 = evidence.ContentSha256.ToLowerInvariant() };
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireRunningPopulationAsync(connection, transaction, populationId, cancellationToken)
            .ConfigureAwait(false);
        var existing = await ReadActiveEvidenceAsync(connection, transaction, normalizedEvidence, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.Matches(normalizedEvidence))
        {
            return new PermalinkPopulationEvidenceResult(existing.RegistryId, Created: false);
        }

        var registryId = StableGuid(
            "evidence",
            string.Join(
                '\0',
                EvidenceKind(normalizedEvidence.Kind),
                normalizedEvidence.RootIdentityId,
                normalizedEvidence.SourceKey,
                normalizedEvidence.PinnedObjectIdentity,
                normalizedEvidence.ContentLength.ToString(CultureInfo.InvariantCulture),
                normalizedEvidence.ContentSha256,
                existing?.RegistryId ?? string.Empty)).ToString("D");
        if (existing is not null)
        {
            using var supersedeScope = await _authority.BeginAuthorizedWriteAsync(
                connection,
                StableGuid("supersede", existing.RegistryId),
                "PermalinkEvidenceRegistry",
                "UPDATE",
                existing.RegistryId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            await using var supersede = connection.CreateCommand();
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE PermalinkEvidenceRegistry
                   SET superseded_by_registry_id = $next, updated_at = $now
                 WHERE registry_id = $id AND superseded_by_registry_id IS NULL AND deleted_at IS NULL
                """;
            supersede.Parameters.AddWithValue("$next", registryId);
            supersede.Parameters.AddWithValue("$now", Now());
            supersede.Parameters.AddWithValue("$id", existing.RegistryId);
            if (await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("registry-evidence-raced", $"Evidence source '{evidence.SourceKey}' changed during population.");
            }
        }

        using (var insertScope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            StableGuid("insert", registryId),
            "PermalinkEvidenceRegistry",
            "INSERT",
            registryId,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PermalinkEvidenceRegistry
                    (registry_id, evidence_kind, root_identity_id, normalized_relative_path,
                     operation_id, state, created_at, updated_at, anchor_token, population_id,
                     source_key, pinned_object_identity, content_length, content_sha256,
                     record_version, superseded_by_registry_id, deleted_at)
                VALUES ($id, $kind, $root, $path, $operation, 'present', $now, $now, NULL,
                        $population, $source, $object, $length, $sha, 1, NULL, NULL)
                """;
            insert.Parameters.AddWithValue("$id", registryId);
            insert.Parameters.AddWithValue("$kind", EvidenceKind(normalizedEvidence.Kind));
            insert.Parameters.AddWithValue("$root", normalizedEvidence.RootIdentityId);
            insert.Parameters.AddWithValue("$path", normalizedEvidence.NormalizedRelativePath);
            insert.Parameters.AddWithValue("$operation", (object?)normalizedEvidence.OperationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", Now());
            insert.Parameters.AddWithValue("$population", populationId);
            insert.Parameters.AddWithValue("$source", normalizedEvidence.SourceKey);
            insert.Parameters.AddWithValue("$object", normalizedEvidence.PinnedObjectIdentity);
            insert.Parameters.AddWithValue("$length", normalizedEvidence.ContentLength);
            insert.Parameters.AddWithValue("$sha", normalizedEvidence.ContentSha256);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermalinkPopulationEvidenceResult(registryId, Created: true);
    }

    /// <summary>Seals one population after every source checkpoint is complete.</summary>
    /// <param name="populationId">The running population identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task CompleteAsync(string populationId, CancellationToken cancellationToken)
    {
        RequireText(populationId, nameof(populationId));
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireRunningPopulationAsync(connection, transaction, populationId, cancellationToken)
            .ConfigureAwait(false);
        await using (var incomplete = connection.CreateCommand())
        {
            incomplete.Transaction = transaction;
            incomplete.CommandText = """
                SELECT COUNT(*) FROM PermalinkRegistryPopulationCheckpoints
                 WHERE population_id = $population AND (status <> 'complete' OR deleted_at IS NOT NULL)
                """;
            incomplete.Parameters.AddWithValue("$population", populationId);
            if (Convert.ToInt64(await incomplete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                throw Conflict(
                    "registry-population-sources-incomplete",
                    $"Registry population '{populationId}' still has incomplete sources.");
            }
        }

        long contentEpoch;
        long cardinality;
        await using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = """
                SELECT content_epoch, (SELECT COUNT(*) FROM PermalinkEvidenceRegistry)
                  FROM AuthorityCounters WHERE singleton = 1
                """;
            await using var reader = await facts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The authority counter row is missing.");
            }

            contentEpoch = reader.GetInt64(0);
            cardinality = reader.GetInt64(1);
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            StableGuid("population-complete", populationId),
            "PermalinkRegistryPopulations",
            "UPDATE",
            populationId,
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE PermalinkRegistryPopulations
                   SET status = 'complete', completed_content_epoch = $epoch,
                       registry_cardinality = $cardinality, updated_at = $now
                 WHERE population_id = $population AND status = 'running'
                """;
            update.Parameters.AddWithValue("$epoch", contentEpoch);
            update.Parameters.AddWithValue("$cardinality", cardinality);
            update.Parameters.AddWithValue("$now", Now());
            update.Parameters.AddWithValue("$population", populationId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("registry-population-not-running", $"Registry population '{populationId}' is not running.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string populationId,
        PermalinkPopulationSource source,
        CancellationToken cancellationToken)
    {
        var key = CheckpointKey(populationId, source);
        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            StableGuid("checkpoint-insert", key),
            "PermalinkRegistryPopulationCheckpoints",
            "INSERT",
            key,
            cancellationToken,
            transaction).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO PermalinkRegistryPopulationCheckpoints
                (population_id, source_kind, source_key, source_generation, cursor_version,
                 cursor, observed_count, status, version, created_at, updated_at, deleted_at)
            VALUES ($population, $kind, $key, $generation, $cursor_version,
                    NULL, 0, 'pending', 1, $now, $now, NULL)
            """;
        insert.Parameters.AddWithValue("$population", populationId);
        insert.Parameters.AddWithValue("$kind", SourceKind(source.Kind));
        insert.Parameters.AddWithValue("$key", source.Key);
        insert.Parameters.AddWithValue("$generation", source.Generation);
        insert.Parameters.AddWithValue("$cursor_version", CursorVersion);
        insert.Parameters.AddWithValue("$now", Now());
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExistingPopulation?> ReadPopulationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string populationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT registry_writer_version, status FROM PermalinkRegistryPopulations WHERE population_id = $id";
        command.Parameters.AddWithValue("$id", populationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExistingPopulation(reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static async Task<bool> MatchesPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string populationId,
        IReadOnlyCollection<PermalinkPopulationSource> sources,
        CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            _ = expected.Add(SourceKind(source.Kind) + "\0" + source.Key + "\0" + source.Generation);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_kind, source_key, source_generation
              FROM PermalinkRegistryPopulationCheckpoints
             WHERE population_id = $population AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$population", populationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = actual.Add(reader.GetString(0) + "\0" + reader.GetString(1) + "\0" + reader.GetString(2));
        }

        return expected.SetEquals(actual);
    }

    private static async Task RequireRunningPopulationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string populationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status FROM PermalinkRegistryPopulations WHERE population_id = $id";
        command.Parameters.AddWithValue("$id", populationId);
        var status = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(status, "running", StringComparison.Ordinal))
        {
            throw Conflict("registry-population-not-running", $"Registry population '{populationId}' is not running.");
        }
    }

    private static async Task<ExistingEvidence?> ReadActiveEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkPopulationEvidence evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT registry_id, normalized_relative_path, operation_id,
                   pinned_object_identity, content_length, content_sha256
              FROM PermalinkEvidenceRegistry
             WHERE evidence_kind = $kind AND root_identity_id = $root AND source_key = $source
               AND superseded_by_registry_id IS NULL AND deleted_at IS NULL
            """;
        command.Parameters.AddWithValue("$kind", EvidenceKind(evidence.Kind));
        command.Parameters.AddWithValue("$root", evidence.RootIdentityId);
        command.Parameters.AddWithValue("$source", evidence.SourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingEvidence(
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5));
    }

    private static void Validate(PermalinkPopulationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireText(source.Key, nameof(source.Key));
        RequireText(source.Generation, nameof(source.Generation));
    }

    private static void Validate(PermalinkPopulationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireText(evidence.RootIdentityId, nameof(evidence.RootIdentityId));
        RequireText(evidence.NormalizedRelativePath, nameof(evidence.NormalizedRelativePath));
        RequireText(evidence.SourceKey, nameof(evidence.SourceKey));
        RequireText(evidence.PinnedObjectIdentity, nameof(evidence.PinnedObjectIdentity));
        if (evidence.ContentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence.ContentLength));
        }

        if (evidence.ContentSha256.Length != 64 || !IsHex(evidence.ContentSha256))
        {
            throw new ArgumentException("The evidence SHA-256 must be 64 hexadecimal characters.", nameof(evidence));
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9')
                  || (character >= 'a' && character <= 'f')
                  || (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireGeneration(PermalinkPopulationSource source, string actual)
    {
        if (!string.Equals(source.Generation, actual, StringComparison.Ordinal))
        {
            throw Conflict(
                "registry-population-source-changed",
                $"Population source '{source.Key}' changed from generation '{actual}' to '{source.Generation}'.");
        }
    }

    private static void RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameter);
        }
    }

    private static string CheckpointKey(string populationId, PermalinkPopulationSource source)
        => populationId + "\0" + SourceKind(source.Kind) + "\0" + source.Key;

    private static string SourceKind(PermalinkPopulationSourceKind kind) => kind switch
    {
        PermalinkPopulationSourceKind.EvidenceRoot => "evidence-root",
        PermalinkPopulationSourceKind.JournalHot => "journal-hot",
        PermalinkPopulationSourceKind.JournalArchive => "journal-archive",
        PermalinkPopulationSourceKind.AuthorityIntent => "authority-intent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string EvidenceKind(PermalinkPopulationEvidenceKind kind) => kind switch
    {
        PermalinkPopulationEvidenceKind.EvidenceFile => "evidence-file",
        PermalinkPopulationEvidenceKind.JournalStaging => "journal-staging",
        PermalinkPopulationEvidenceKind.JournalQuarantine => "journal-quarantine",
        PermalinkPopulationEvidenceKind.RetirementCopy => "retirement-copy",
        PermalinkPopulationEvidenceKind.MutationIntent => "mutation-intent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Guid StableGuid(string domain, string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\0" + value));
        Span<byte> bytes = stackalloc byte[16];
        digest.AsSpan(0, bytes.Length).CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private string Now() => _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private static PermalinkException VersionRefused(int version)
        => new(
            PermalinkErrorKind.VersionRefused,
            "registry-population-writer-version",
            $"Registry population writer version {version} is not supported by this binary ({WriterVersion}).");

    /// <summary>One immutable source identity and generation in a population plan.</summary>
    internal sealed record PermalinkPopulationSource(
        PermalinkPopulationSourceKind Kind,
        string Key,
        string Generation);

    /// <summary>One physical or logical evidence observation ready for durable registration.</summary>
    internal sealed record PermalinkPopulationEvidence(
        PermalinkPopulationEvidenceKind Kind,
        string RootIdentityId,
        string NormalizedRelativePath,
        string SourceKey,
        string? OperationId,
        string PinnedObjectIdentity,
        long ContentLength,
        string ContentSha256);

    /// <summary>The durable checkpoint for one independently resumable population source.</summary>
    internal sealed record PermalinkPopulationCheckpoint(
        int CursorVersion,
        string? Cursor,
        long ObservedCount,
        bool Complete);

    /// <summary>The idempotent result of registering one evidence observation.</summary>
    internal sealed record PermalinkPopulationEvidenceResult(string RegistryId, bool Created);

    private sealed record ExistingEvidence(
        string RegistryId,
        string NormalizedRelativePath,
        string? OperationId,
        string PinnedObjectIdentity,
        long ContentLength,
        string ContentSha256)
    {
        public bool Matches(PermalinkPopulationEvidence evidence)
            => string.Equals(NormalizedRelativePath, evidence.NormalizedRelativePath, StringComparison.Ordinal)
                && string.Equals(OperationId, evidence.OperationId, StringComparison.Ordinal)
                && string.Equals(PinnedObjectIdentity, evidence.PinnedObjectIdentity, StringComparison.Ordinal)
                && ContentLength == evidence.ContentLength
                && string.Equals(ContentSha256, evidence.ContentSha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ExistingPopulation(int WriterVersion, string Status);
}
