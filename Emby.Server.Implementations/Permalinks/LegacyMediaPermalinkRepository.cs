// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Reads previously issued alias-to-item associations for migration only. New
/// installations do not need this database. No media is opened or identified.
/// </summary>
public sealed class LegacyMediaPermalinkRepository(IPermalinkAuthorityRootProvider roots)
{
    internal async Task<IReadOnlyList<string>> ReadItemAliasesAsync(Guid itemId, CancellationToken token)
    {
        await using var connection = await OpenExistingAsync(token).ConfigureAwait(false);
        if (connection is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.PermalinkId
                  FROM PermalinkBindings b
                  LEFT JOIN FirstAliasClaims f ON f.permalink_id = b.PermalinkId
                 WHERE b.ItemId = $item
                 GROUP BY b.PermalinkId
                 ORDER BY MIN(COALESCE(f.created_at, b.CreatedAt)), b.PermalinkId
                """;
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            var aliases = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                aliases.Add(reader.GetString(0));
            }

            return aliases;
        }
        catch (SqliteException exception)
        {
            throw ReadFailure(exception);
        }
    }

    internal async Task<IReadOnlyList<LegacyAssociation>> ReadAsync(string alias, CancellationToken token)
    {
        await using var connection = await OpenExistingAsync(token).ConfigureAwait(false);
        if (connection is null)
        {
            return Array.Empty<LegacyAssociation>();
        }

        try
        {
            var result = new List<LegacyAssociation>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT DISTINCT b.ItemId, related.PermalinkId
                      FROM PermalinkBindings b
                      JOIN PermalinkBindings related ON related.ItemId = b.ItemId
                     WHERE b.PermalinkId = $alias
                     ORDER BY b.ItemId, related.PermalinkId
                    """;
                command.Parameters.AddWithValue("$alias", alias);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!Guid.TryParse(reader.GetString(0), out var itemId))
                    {
                        throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-item-id-invalid",
                            "A previously issued permalink has an invalid library item ID.");
                    }

                    result.Add(new LegacyAssociation(itemId, itemId, reader.GetString(1), null, null));
                }
            }

            // Conversions can remove an old item or leave its derived binding
            // behind. In either case, also retain the saved filename/metadata
            // association for the ordinary library lookup. A derived binding
            // alone is not evidence that its library record still exists.
            await using var orphan = connection.CreateCommand();
            orphan.CommandText = """
                SELECT f.capsule_id, a.current_path, a.event_json, f.event_json, a.item_kind
                  FROM FirstAliasClaims f
                  JOIN AnchorTokenCapsules a ON a.capsule_id = f.capsule_id
                 WHERE f.permalink_id = $alias
                 ORDER BY f.created_at, f.capsule_id
                """;
            orphan.Parameters.AddWithValue("$alias", alias);
            await using var orphanReader = await orphan.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await orphanReader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!Guid.TryParse(orphanReader.GetString(0), out var groupId))
                {
                    throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-association-id-invalid",
                        "A previously issued permalink has an invalid association ID.");
                }

                var path = orphanReader.GetString(1);
                var metadata = ReadEvent(orphanReader.GetString(2), groupId);
                var issuance = ReadEvent(orphanReader.GetString(3), groupId);
                var aliases = metadata.ActiveExternalAliases.Concat(issuance.ActiveExternalAliases)
                    .Concat(metadata.Ids.Select(value => value.Id)).Concat(issuance.Ids.Select(value => value.Id))
                    .Append(alias).Distinct(StringComparer.Ordinal);
                foreach (var recordedAlias in aliases)
                {
                    if (!MediaContentIdentifier.IsPublicAlias(recordedAlias))
                    {
                        throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-alias-invalid",
                            "A previously issued permalink contains an unsupported alias.");
                    }

                    result.Add(new LegacyAssociation(groupId, null, recordedAlias, path, orphanReader.GetString(4)));
                }
            }

            return result;
        }
        catch (SqliteException exception)
        {
            throw ReadFailure(exception);
        }
    }

    private async Task<SqliteConnection?> OpenExistingAsync(CancellationToken token)
    {
        var root = roots.AuthorityRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var path = Path.Combine(root, ".sloptank", "permalinks", "authority.db");
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            // An installation that never issued legacy links needs no import.
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // The optional legacy authority was never provisioned here.
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PermalinkException(PermalinkErrorKind.Unavailable, "legacy-alias-read-failed",
                $"Cannot inspect previously issued permalink storage: {exception.Message}", exception);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await PermalinkAuthoritySchema.ValidateProtocolEpochAsync(connection, token).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw ReadFailure(exception);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static PermalinkEventDocument ReadEvent(string json, Guid associationId)
    {
        PermalinkEventDocument document;
        try
        {
            document = CanonicalJson.Deserialize<PermalinkEventDocument>(Encoding.UTF8.GetBytes(json), $"legacy association {associationId}");
        }
        catch (PermalinkException exception) when (exception.Code == "malformed-document")
        {
            throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-event-invalid", exception.Message, exception);
        }

        if (document.Version != 1)
        {
            throw new PermalinkException(PermalinkErrorKind.VersionRefused, "legacy-event-version-unsupported",
                $"Saved alias metadata version {document.Version} is not supported.");
        }

        if (document.Type != "sloptank.permalink-event" || document.Ids is null
            || document.Ids.Any(value => value is null || string.IsNullOrEmpty(value.Id)) || document.ActiveExternalAliases is null)
        {
            throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-event-invalid",
                "The saved alias metadata has an unsupported format.");
        }

        return document;
    }

    private static PermalinkException ReadFailure(SqliteException exception) => new(PermalinkErrorKind.Unavailable, "legacy-alias-read-failed",
        $"Cannot read previously issued permalinks (SQLite {exception.SqliteErrorCode}: {exception.Message}).", exception);

    internal sealed record LegacyAssociation(Guid GroupId, Guid? ItemId, string Alias, string? Path, string? ItemKind);
}
