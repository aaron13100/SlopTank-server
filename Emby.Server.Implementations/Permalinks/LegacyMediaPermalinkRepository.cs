// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using System;
using System.Collections.Generic;
using System.IO;
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
    internal async Task<IReadOnlyList<LegacyAssociation>> ReadAsync(string alias, CancellationToken token)
    {
        var root = roots.AuthorityRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Array.Empty<LegacyAssociation>();
        }

        var path = Path.Combine(root, ".sloptank", "permalinks", "authority.db");
        if (!File.Exists(path))
        {
            return Array.Empty<LegacyAssociation>();
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await PermalinkAuthoritySchema.ValidateProtocolEpochAsync(connection, token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT b.ItemId, related.PermalinkId
                  FROM PermalinkBindings b
                  JOIN PermalinkBindings related ON related.ItemId = b.ItemId
                 WHERE b.PermalinkId = $alias
                 ORDER BY b.ItemId, related.PermalinkId
                """;
            command.Parameters.AddWithValue("$alias", alias);
            var result = new List<LegacyAssociation>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var itemId))
                {
                    throw new PermalinkException(PermalinkErrorKind.Corrupt, "legacy-item-id-invalid",
                        "A previously issued permalink has an invalid library item ID.");
                }

                result.Add(new LegacyAssociation(itemId, reader.GetString(1)));
            }

            return result;
        }
        catch (SqliteException exception)
        {
            throw new PermalinkException(PermalinkErrorKind.Unavailable, "legacy-alias-read-failed",
                $"Cannot read previously issued permalinks (SQLite {exception.SqliteErrorCode}: {exception.Message}).", exception);
        }
    }

    internal sealed record LegacyAssociation(Guid ItemId, string Alias);
}
