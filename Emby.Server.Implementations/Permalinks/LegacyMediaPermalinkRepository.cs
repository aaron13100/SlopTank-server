// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03, 2026-11-04.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Reads issued legacy aliases once for migration, without changing the rollback database.</summary>
public sealed class LegacyMediaPermalinkRepository
{
    /// <summary>Reads old alias associations. No connection to this store is used during resolution.</summary>
    /// <param name="databasePath">The existing legacy database or a read-only dry-run copy.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>Every issued alias, including aliases whose old item no longer exists.</returns>
    public async Task<IReadOnlyList<LegacyAlias>> ReadAsync(string databasePath, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        var aliases = new Dictionary<string, LegacyAlias>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PermalinkId FROM PermalinkIssuances UNION SELECT PermalinkId FROM PermalinkBindings UNION SELECT permalink_id FROM FirstAliasClaims ORDER BY 1";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var alias = reader.GetString(0);
                aliases.Add(alias, new LegacyAlias(alias));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT PermalinkId, ItemId FROM PermalinkBindings ORDER BY CreatedAt, PermalinkId";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var row = aliases[reader.GetString(0)];
                if (Guid.TryParse(reader.GetString(1), out var itemId))
                {
                    row.ItemIds.Add(itemId);
                }
                else
                {
                    row.Error = "legacy-item-id-invalid";
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT f.permalink_id, a.item_kind, a.current_path, f.event_json, a.event_json
                FROM FirstAliasClaims f LEFT JOIN AnchorTokenCapsules a ON a.capsule_id = f.capsule_id
                ORDER BY f.created_at, f.permalink_id
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var row = aliases[reader.GetString(0)];
                if (!await reader.IsDBNullAsync(1, token).ConfigureAwait(false))
                {
                    row.Kinds.Add(reader.GetString(1));
                }

                if (!await reader.IsDBNullAsync(2, token).ConfigureAwait(false))
                {
                    row.RecordedPaths.Add(reader.GetString(2));
                }

                for (var column = 3; column <= 4; column++)
                {
                    if (await reader.IsDBNullAsync(column, token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(reader.GetString(column));
                        var value = document.RootElement;
                        if (value.GetProperty("type").GetString() != "sloptank.permalink-event" || value.GetProperty("version").GetInt32() != 1)
                        {
                            row.Error = "legacy-event-version-unsupported";
                            continue;
                        }

                        foreach (var alias in value.GetProperty("active_external_aliases").EnumerateArray().Select(element => element.GetString()))
                        {
                            if (alias is not null && MediaContentIdentifier.ParseProviderAlias(alias) is not null)
                            {
                                row.ProviderAliases.Add(alias);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
                    {
                        row.Error = $"legacy-event-invalid: {exception.Message}";
                    }
                }
            }
        }

        return aliases.Values.ToArray();
    }

    /// <summary>Old associations used only by the one-time importer.</summary>
    /// <param name="Alias">The issued public URL identifier.</param>
    public sealed record LegacyAlias(string Alias)
    {
        /// <summary>Gets the old library record IDs.</summary>
        public HashSet<Guid> ItemIds { get; } = [];

        /// <summary>Gets the saved provider identifiers.</summary>
        public HashSet<string> ProviderAliases { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the saved locators, used only to locate old records during import.</summary>
        public HashSet<string> RecordedPaths { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the saved media kinds, retaining ambiguous aliases across titles.</summary>
        public HashSet<string> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets or sets a malformed-record diagnostic to put in the casualty report.</summary>
        public string? Error { get; set; }
    }
}
