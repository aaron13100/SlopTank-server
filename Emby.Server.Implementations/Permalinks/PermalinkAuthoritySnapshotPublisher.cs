// SlopTank modification notice: added or changed by SlopTank on 2026-09-19, 2026-10-01, 2026-10-07, 2026-10-08, 2026-10-09.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Publishes immutable, manifest-bound public authority snapshots.</summary>
internal sealed class PermalinkAuthoritySnapshotPublisher : IPermalinkAuthorityGenerationOracle, IDisposable
{
    private static readonly Regex GenerationNameRegex =
        new("g-(?<generation>[1-9][0-9]*)-(?<nonce>[0-9a-f]{32})", RegexOptions.Compiled);

    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _publicationLock = new(1, 1);
    private readonly string _snapshotRoot;

    public PermalinkAuthoritySnapshotPublisher(
        PermalinkAuthorityStore authority,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
        _snapshotRoot = configuration["Permalinks:SnapshotRoot"]
            ?? "/Users/Shared/SlopTank/permalink-authority-snapshots";
    }

    public void Dispose() => _publicationLock.Dispose();

    /// <inheritdoc />
    public async Task<PermalinkAuthorityGenerationInfo> GetGenerationAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT authority_sequence, content_epoch, journal_counter,
                   COALESCE((SELECT MAX(generation) FROM SnapshotMetadata), 0)
              FROM AuthorityCounters WHERE singleton = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Corrupt,
                "authority-counters-missing",
                "The permalink authority counter row is missing.");
        }

        return new PermalinkAuthorityGenerationInfo(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            PermalinkAuthoritySchema.SupportedProtocolEpoch);
    }

    /// <summary>
    /// Gets a value indicating whether <c>CURRENT</c> already publishes the live authority:
    /// same authority id, schema epoch, content epoch and journal counter. Any missing,
    /// malformed or foreign pointer reads as stale so the lifecycle republishes rather than
    /// trusting an unverified file. This is a bounded freshness probe, never a validation of
    /// the published bytes; readers keep their own fail-closed validation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the published pointer matches the live authority counters.</returns>
    internal async Task<bool> IsCurrentFreshAsync(CancellationToken cancellationToken)
    {
        string pointer;
        try
        {
            pointer = await File.ReadAllTextAsync(
                Path.Combine(_snapshotRoot, "CURRENT"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!pointer.EndsWith('\n') || pointer.Count(character => character == '\n') != 1)
        {
            return false;
        }

        var name = pointer[..^1];
        if (!GenerationNameRegex.IsMatch(name))
        {
            return false;
        }

        var directory = Path.Combine(_snapshotRoot, name);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!Directory.Exists(directory)
            || !File.Exists(manifestPath)
            || !File.Exists(Path.Combine(directory, "authority.db")))
        {
            return false;
        }

        PermalinkAuthorityGeneration manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PermalinkAuthorityGeneration>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return false;
        }

        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_epoch, journal_counter FROM AuthorityCounters WHERE singleton = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var authorityId = await ReadAuthorityIdAsync(_authority.AuthorityRoot, cancellationToken)
            .ConfigureAwait(false);
        return manifest.Generation == long.Parse(
                GenerationNameRegex.Match(name).Groups["generation"].Value, CultureInfo.InvariantCulture)
            && manifest.AuthorityId == authorityId
            && manifest.SchemaEpoch == PermalinkAuthoritySchema.SupportedProtocolEpoch
            && manifest.ContentEpoch == reader.GetInt64(0)
            && manifest.JournalCounter == reader.GetInt64(1);
    }

    /// <summary>Publishes one database/manifest pair and atomically advances CURRENT.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The published generation manifest.</returns>
    public async Task<PermalinkAuthorityGeneration> PublishAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await _publicationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_snapshotRoot);
            await using var source = await _authority.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var metadata = await AllocateGenerationAsync(source, cancellationToken)
                .ConfigureAwait(false);
            // The pin must be a DEFERRED serializable read transaction. Microsoft.Data.Sqlite
            // 10.0.10 has no async overload that preserves both properties, so the helper contains
            // its unavoidable synchronous transaction-construction boundary. BEGIN IMMEDIATE
            // would make SQLite's online backup return SQLITE_BUSY ("database is locked") at once.
            await using var pinned = PermalinkSqliteTransactions.BeginDeferredSerializable(source);
            await PinGenerationAsync(source, pinned, metadata, cancellationToken)
                .ConfigureAwait(false);
            var directoryName = $"g-{metadata.Generation.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
            var temporary = Path.Combine(_snapshotRoot, $".tmp-{directoryName}");
            var published = Path.Combine(_snapshotRoot, directoryName);
            Directory.CreateDirectory(temporary);
            try
            {
                var database = Path.Combine(temporary, "authority.db");
                await using (var target = new SqliteConnection($"Data Source={database};Pooling=False"))
                {
                    await target.OpenAsync(cancellationToken).ConfigureAwait(false);
                    source.BackupDatabase(target);
                }

                await VerifyCopiedGenerationAsync(database, metadata, cancellationToken)
                    .ConfigureAwait(false);

                var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(
                    database, cancellationToken).ConfigureAwait(false)));
                var manifest = metadata with
                {
                    DatabaseLength = new FileInfo(database).Length,
                    DatabaseSha256 = digest
                };
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "manifest.json"),
                    JsonSerializer.Serialize(manifest) + "\n",
                    cancellationToken).ConfigureAwait(false);
                PermalinkSnapshotDurability.FlushFile(database);
                PermalinkSnapshotDurability.FlushFile(Path.Combine(temporary, "manifest.json"));
                PermalinkSnapshotDurability.FlushDirectory(temporary);
                Directory.Move(temporary, published);
                MakePublishedGenerationImmutable(published);
                PermalinkSnapshotDurability.FlushDirectory(_snapshotRoot);
                var next = Path.Combine(_snapshotRoot, "CURRENT.new");
                await File.WriteAllTextAsync(next, directoryName + "\n", cancellationToken)
                    .ConfigureAwait(false);
                PermalinkSnapshotDurability.FlushFile(next);
                File.Move(next, Path.Combine(_snapshotRoot, "CURRENT"), overwrite: true);
                PermalinkSnapshotDurability.FlushDirectory(_snapshotRoot);
                return manifest;
            }
            catch
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }

                throw;
            }
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            // UnauthorizedAccessException is how POSIX EISDIR ("path is a directory")
            // surfaces from File.WriteAllTextAsync, so it belongs to the same typed
            // publication-failure boundary as ordinary IO errors.
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "authority-snapshot-unavailable",
                $"The permalink authority snapshot could not be published ({exception.Message}).",
                exception);
        }
        finally
        {
            _publicationLock.Release();
        }
    }

    private static void MakePublishedGenerationImmutable(string published)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(
                Path.Combine(published, "authority.db"),
                FileAttributes.ReadOnly);
            File.SetAttributes(
                Path.Combine(published, "manifest.json"),
                FileAttributes.ReadOnly);
            return;
        }

        File.SetUnixFileMode(
            Path.Combine(published, "authority.db"),
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(
            Path.Combine(published, "manifest.json"),
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(
            published,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task PinGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkAuthorityGeneration expected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT authority_id, schema_epoch, content_epoch, journal_counter, created_at
              FROM SnapshotMetadata WHERE generation = $generation
            """;
        command.Parameters.AddWithValue("$generation", expected.Generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetString(0) != expected.AuthorityId
            || reader.GetInt64(1) != expected.SchemaEpoch
            || reader.GetInt64(2) != expected.ContentEpoch
            || reader.GetInt64(3) != expected.JournalCounter
            || reader.GetString(4) != expected.CreatedAt)
        {
            throw new InvalidDataException("snapshot metadata changed before the pinned read");
        }
    }

    private static async Task VerifyCopiedGenerationAsync(
        string database,
        PermalinkAuthorityGeneration expected,
        CancellationToken cancellationToken)
    {
        await using var copy = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = copy.CreateCommand();
        command.CommandText = """
            SELECT authority_id, schema_epoch, content_epoch, journal_counter, created_at
              FROM SnapshotMetadata WHERE generation = $generation
            """;
        command.Parameters.AddWithValue("$generation", expected.Generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetString(0) != expected.AuthorityId
            || reader.GetInt64(1) != expected.SchemaEpoch
            || reader.GetInt64(2) != expected.ContentEpoch
            || reader.GetInt64(3) != expected.JournalCounter
            || reader.GetString(4) != expected.CreatedAt)
        {
            throw new InvalidDataException("copied snapshot metadata does not match its generation");
        }
    }

    private async Task<PermalinkAuthorityGeneration> AllocateGenerationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var authorityId = await ReadAuthorityIdAsync(_authority.AuthorityRoot, cancellationToken)
            .ConfigureAwait(false);
        var created = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await using var counters = connection.CreateCommand();
        counters.CommandText = "SELECT content_epoch, journal_counter FROM AuthorityCounters WHERE singleton = 1";
        await using var counterReader = await counters.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await counterReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("authority counter row is missing");
        }

        var contentEpoch = counterReader.GetInt64(0);
        var journalCounter = counterReader.GetInt64(1);
        await counterReader.DisposeAsync().ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO SnapshotMetadata
                (authority_id, schema_epoch, content_epoch, journal_counter, created_at)
            VALUES ($authority, $schema, $content, $journal, $created);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$authority", authorityId);
        insert.Parameters.AddWithValue("$schema", PermalinkAuthoritySchema.SupportedProtocolEpoch);
        insert.Parameters.AddWithValue("$content", contentEpoch);
        insert.Parameters.AddWithValue("$journal", journalCounter);
        insert.Parameters.AddWithValue("$created", created);
        var generation = Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new PermalinkAuthorityGeneration(
            generation,
            authorityId,
            PermalinkAuthoritySchema.SupportedProtocolEpoch,
            contentEpoch,
            journalCounter,
            created,
            0,
            string.Empty);
    }

    /// <summary>Reads the authority identity the snapshot must be bound to.</summary>
    /// <param name="authorityRoot">The authority root directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authority id from authority.json.</returns>
    private static async Task<string> ReadAuthorityIdAsync(
        string authorityRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(authorityRoot, "authority.json"), cancellationToken)
                .ConfigureAwait(false));
            return document.RootElement.GetProperty("authority_id").GetString()
                ?? throw new InvalidDataException("authority.json lacks authority_id");
        }
        catch (Exception exception)
            when (exception is JsonException
                or InvalidDataException
                or KeyNotFoundException
                or InvalidOperationException)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Corrupt,
                "authority-identity-corrupt",
                "The permalink authority identity document is malformed.",
                exception);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "authority-identity-unavailable",
                $"The permalink authority identity document could not be read ({exception.Message}).",
                exception);
        }
    }
}
