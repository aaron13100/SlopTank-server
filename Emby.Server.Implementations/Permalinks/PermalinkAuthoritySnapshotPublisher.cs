// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Publishes immutable, manifest-bound public authority snapshots.</summary>
internal sealed class PermalinkAuthoritySnapshotPublisher : IDisposable
{
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
                Directory.Move(temporary, published);
                var next = Path.Combine(_snapshotRoot, "CURRENT.new");
                await File.WriteAllTextAsync(next, directoryName + "\n", cancellationToken)
                    .ConfigureAwait(false);
                File.Move(next, Path.Combine(_snapshotRoot, "CURRENT"), overwrite: true);
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
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
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

    private async Task<PermalinkAuthorityGeneration> AllocateGenerationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var authorityDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(_authority.AuthorityRoot, "authority.json"), cancellationToken)
            .ConfigureAwait(false));
        var authorityId = authorityDocument.RootElement.GetProperty("authority_id").GetString()
            ?? throw new InvalidDataException("authority.json lacks authority_id");
        var created = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO SnapshotMetadata
                (authority_id, schema_epoch, content_epoch, journal_counter, created_at)
            VALUES ($authority, $schema, 0, 0, $created);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$authority", authorityId);
        insert.Parameters.AddWithValue("$schema", PermalinkAuthoritySchema.SupportedProtocolEpoch);
        insert.Parameters.AddWithValue("$created", created);
        var generation = Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new PermalinkAuthorityGeneration(
            generation,
            authorityId,
            PermalinkAuthoritySchema.SupportedProtocolEpoch,
            0,
            0,
            created,
            0,
            string.Empty);
    }
}
