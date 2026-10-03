// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Seals and verifies the exact registry/root/journal/content tuple required for absence.</summary>
internal sealed class PermalinkAbsenceWatermarkService
{
    /// <summary>The complete registry population contract understood by this binary.</summary>
    public const int RegistryWriterVersion = 1;

    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournalCensus _journal;
    private readonly TimeProvider _timeProvider;

    public PermalinkAbsenceWatermarkService(
        PermalinkAuthorityStore authority,
        PermalinkOperationJournalCensus journal,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _journal = journal;
        _timeProvider = timeProvider;
    }

    /// <summary>Seals one stable tuple after a complete current-version registry population.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The immutable watermark.</returns>
    public Task<PermalinkAbsenceWatermark> SealAsync(CancellationToken cancellationToken)
        => GuardAsync(() => SealCoreAsync(cancellationToken), cancellationToken);

    /// <summary>Recomputes every bound fact and returns the latest watermark only when still exact.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The still-current watermark.</returns>
    public Task<PermalinkAbsenceWatermark> VerifyLatestAsync(CancellationToken cancellationToken)
        => GuardAsync(() => VerifyLatestCoreAsync(cancellationToken), cancellationToken);

    private async Task<PermalinkAbsenceWatermark> SealCoreAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var baseline = await ReadFactsAsync(connection: null, transaction: null, cancellationToken)
            .ConfigureAwait(false);
        ValidatePopulation(baseline);
        var journal = await _journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        var repeated = await _journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (journal != repeated)
        {
            throw Conflict(
                "absence-watermark-journal-raced",
                "The operation journal changed while its absence generation was being measured.");
        }

        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadFactsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        ValidatePopulation(current);
        if (baseline != current)
        {
            throw Conflict(
                "absence-watermark-authority-raced",
                "The authority changed while its absence watermark was being assembled.");
        }

        var watermarkId = Guid.NewGuid();
        var createdAt = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var watermark = new PermalinkAbsenceWatermark(
            watermarkId,
            current.RootGenerationsJson,
            journal,
            RegistryWriterVersion,
            current.ContentEpoch,
            current.JournalCounter,
            current.RegistryCardinality,
            current.PopulationId!,
            createdAt);
        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            watermarkId,
            "PermalinkAbsenceWatermarks",
            "INSERT",
            watermarkId.ToString("D"),
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO PermalinkAbsenceWatermarks
                    (watermark_id, root_generations_json, hot_journal_generation,
                     hot_journal_cardinality, archive_journal_generation,
                     archive_journal_cardinality, registry_writer_version, content_epoch,
                     journal_counter, registry_cardinality, population_id, created_at)
                VALUES ($id, $roots, $hot, $hot_count, $archive, $archive_count,
                        $writer, $content, $journal, $registry, $population, $created)
                """;
            command.Parameters.AddWithValue("$id", watermarkId.ToString("D"));
            command.Parameters.AddWithValue("$roots", watermark.RootGenerationsJson);
            command.Parameters.AddWithValue("$hot", watermark.Journal.HotGeneration);
            command.Parameters.AddWithValue("$hot_count", watermark.Journal.HotCardinality);
            command.Parameters.AddWithValue("$archive", watermark.Journal.ArchiveGeneration);
            command.Parameters.AddWithValue("$archive_count", watermark.Journal.ArchiveCardinality);
            command.Parameters.AddWithValue("$writer", watermark.RegistryWriterVersion);
            command.Parameters.AddWithValue("$content", watermark.ContentEpoch);
            command.Parameters.AddWithValue("$journal", watermark.JournalCounter);
            command.Parameters.AddWithValue("$registry", watermark.RegistryCardinality);
            command.Parameters.AddWithValue("$population", watermark.PopulationId);
            command.Parameters.AddWithValue("$created", watermark.CreatedAt);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return watermark;
    }

    private async Task<PermalinkAbsenceWatermark> VerifyLatestCoreAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var watermark = await ReadLatestAsync(connection, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("absence-watermark-missing", "No absence watermark has been sealed.");
        var facts = await ReadFactsAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        if (watermark.RegistryWriterVersion != RegistryWriterVersion
            || watermark.ContentEpoch != facts.ContentEpoch
            || watermark.JournalCounter != facts.JournalCounter
            || watermark.RegistryCardinality != facts.RegistryCardinality
            || !string.Equals(watermark.RootGenerationsJson, facts.RootGenerationsJson, StringComparison.Ordinal)
            || !string.Equals(watermark.PopulationId, facts.PopulationId, StringComparison.Ordinal))
        {
            throw Conflict(
                "absence-watermark-authority-stale",
                "The authority/root/registry tuple changed after the absence watermark was sealed.");
        }

        ValidatePopulation(facts);
        var journal = await _journal.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (watermark.Journal != journal)
        {
            throw Conflict(
                "absence-watermark-journal-stale",
                "The complete hot/archive operation journal changed after the absence watermark was sealed.");
        }

        var after = await ReadFactsAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        if (facts != after)
        {
            throw Conflict(
                "absence-watermark-authority-stale",
                "The authority changed while the absence watermark was being verified.");
        }

        return watermark;
    }

    private async Task<AuthorityFacts> ReadFactsAsync(
        SqliteConnection? connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var ownsConnection = connection is null;
        connection ??= await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long contentEpoch;
            long journalCounter;
            long registryCardinality;
            await using (var counters = connection.CreateCommand())
            {
                counters.Transaction = transaction;
                counters.CommandText = """
                    SELECT content_epoch, journal_counter,
                           (SELECT COUNT(*) FROM PermalinkEvidenceRegistry)
                      FROM AuthorityCounters WHERE singleton = 1
                    """;
                await using var reader = await counters.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("authority counter row is missing");
                }

                contentEpoch = reader.GetInt64(0);
                journalCounter = reader.GetInt64(1);
                registryCardinality = reader.GetInt64(2);
            }

            var roots = new SortedDictionary<string, long>(StringComparer.Ordinal);
            await using (var rootCommand = connection.CreateCommand())
            {
                rootCommand.Transaction = transaction;
                rootCommand.CommandText = """
                    SELECT root_identity_id, generation FROM PermalinkRootIdentities
                     ORDER BY root_identity_id COLLATE BINARY
                    """;
                await using var reader = await rootCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    roots.Add(reader.GetString(0), reader.GetInt64(1));
                }
            }

            string? populationId = null;
            string? populationStatus = null;
            int? writerVersion = null;
            long? populationEpoch = null;
            long? populationCardinality = null;
            await using (var population = connection.CreateCommand())
            {
                population.Transaction = transaction;
                population.CommandText = """
                    SELECT population_id, status, registry_writer_version,
                           completed_content_epoch, registry_cardinality
                      FROM PermalinkRegistryPopulations
                     ORDER BY rowid DESC LIMIT 1
                    """;
                await using var reader = await population.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    populationId = reader.GetString(0);
                    populationStatus = reader.GetString(1);
                    writerVersion = reader.GetInt32(2);
                    if (!await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
                    {
                        populationEpoch = reader.GetInt64(3);
                    }

                    if (!await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false))
                    {
                        populationCardinality = reader.GetInt64(4);
                    }
                }
            }

            return new AuthorityFacts(
                JsonSerializer.Serialize(roots),
                contentEpoch,
                journalCounter,
                registryCardinality,
                populationId,
                populationStatus,
                writerVersion,
                populationEpoch,
                populationCardinality);
        }
        finally
        {
            if (ownsConnection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ValidatePopulation(AuthorityFacts facts)
    {
        if (facts.PopulationId is null
            || !string.Equals(facts.PopulationStatus, "complete", StringComparison.Ordinal))
        {
            throw Conflict(
                "absence-registry-population-incomplete",
                "The latest registry population is not complete for absence proof.");
        }

        if (facts.PopulationWriterVersion != RegistryWriterVersion)
        {
            throw new PermalinkException(
                PermalinkErrorKind.VersionRefused,
                "absence-registry-writer-version",
                $"Registry population writer version {facts.PopulationWriterVersion} does not match supported version {RegistryWriterVersion}.");
        }

        if (facts.PopulationContentEpoch != facts.ContentEpoch
            || facts.PopulationRegistryCardinality != facts.RegistryCardinality)
        {
            throw Conflict(
                "absence-registry-population-incomplete",
                "The completed registry population does not match the current content epoch and cardinality.");
        }
    }

    private static async Task<PermalinkAbsenceWatermark?> ReadLatestAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT watermark_id, root_generations_json, hot_journal_generation,
                   hot_journal_cardinality, archive_journal_generation,
                   archive_journal_cardinality, registry_writer_version, content_epoch,
                   journal_counter, registry_cardinality, population_id, created_at
              FROM PermalinkAbsenceWatermarks ORDER BY rowid DESC LIMIT 1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PermalinkAbsenceWatermark(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            new PermalinkJournalCensus(
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5)),
            reader.GetInt32(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetString(11));
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or SqliteException
                or JsonException
                or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "absence-watermark-unavailable",
                $"The absence watermark failed closed ({exception.Message}).",
                exception);
        }
    }

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private sealed record AuthorityFacts(
        string RootGenerationsJson,
        long ContentEpoch,
        long JournalCounter,
        long RegistryCardinality,
        string? PopulationId,
        string? PopulationStatus,
        int? PopulationWriterVersion,
        long? PopulationContentEpoch,
        long? PopulationRegistryCardinality);
}
