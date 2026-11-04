// SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Imports old URLs once through the normal startup migration, retaining the old database for rollback.</summary>
/// <param name="legacy">The read-only old alias reader.</param>
/// <param name="repository">The application alias table.</param>
/// <param name="manager">The logical metadata association service.</param>
/// <param name="library">The existing library query service.</param>
/// <param name="configuration">The old database location configuration.</param>
/// <param name="paths">The application's report directory.</param>
/// <param name="clock">The timestamp provider.</param>
/// <param name="logger">The migration diagnostic logger.</param>
public sealed class LegacyPermalinkImport(
    LegacyMediaPermalinkRepository legacy,
    MediaPermalinkRepository repository,
    PermalinkManager manager,
    ILibraryManager library,
    IConfiguration configuration,
    IApplicationPaths paths,
    TimeProvider clock,
    ILogger<LegacyPermalinkImport> logger) : ILegacyPermalinkImporter
{
    /// <summary>Imports aliases and writes unmappable aliases and missing library records to a report.</summary>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>The asynchronous migration operation.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = configuration["Permalinks:AuthorityRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var databasePath = Path.Combine(root, ".sloptank", "permalinks", "authority.db");
        if (!File.Exists(databasePath))
        {
            // Fresh installations have no legacy URLs to import.
            return;
        }

        var reportPath = Path.Combine(paths.DataPath, "legacy-permalink-import.jsonl");
        await using var report = new StreamWriter(reportPath, append: false) { AutoFlush = true };
        IReadOnlyList<LegacyMediaPermalinkRepository.LegacyAlias> aliases;
        try
        {
            aliases = await legacy.ReadAsync(databasePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Cannot read legacy aliases; preserved rollback database {DatabasePath}. See {ReportPath}", databasePath, reportPath);
            await report.WriteLineAsync(JsonSerializer.Serialize(new { Status = "legacy-database-unreadable", DatabasePath = databasePath, Error = exception.Message })).ConfigureAwait(false);
            return;
        }

        var items = library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.BoxSet, BaseItemKind.Video, BaseItemKind.MusicVideo]
        }).Where(item => !item.ExtraType.HasValue).ToArray();
        var snapshot = new List<LibraryRecord>(items.Length);
        foreach (var item in items)
        {
            try
            {
                var identity = await manager.ReadMetadataAsync(item, createSeriesIdentity: false, cancellationToken).ConfigureAwait(false);
                // This is a migration diagnostic only. A locator's existence does
                // not supply identity and is never consulted by permalink lookup.
                var missing = item is Video && item.IsFileProtocol && !item.IsVirtualItem && !string.IsNullOrEmpty(item.Path) && !File.Exists(item.Path);
                snapshot.Add(new LibraryRecord(item.Id, identity.Kind, identity.PublicAliases, item.Path, missing));
            }
            catch (PermalinkException exception)
            {
                logger.LogWarning(exception, "Cannot read logical identity for legacy import item {ItemId}: {Code}", item.Id, exception.Code);
                await report.WriteLineAsync(JsonSerializer.Serialize(new { Status = "library-metadata-invalid", ItemId = item.Id, Error = $"{exception.Code}: {exception.Message}" })).ConfigureAwait(false);
            }
        }

        foreach (var missing in snapshot.Where(item => item.MissingFile))
        {
            await report.WriteLineAsync(JsonSerializer.Serialize(new { Status = "library-file-missing", missing.ItemId, missing.Path, missing.PublicAliases })).ConfigureAwait(false);
        }

        var byId = items.ToDictionary(item => item.Id);
        var imported = 0;
        var casualties = 0;
        foreach (var plan in Plan(aliases, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Error is not null)
            {
                casualties++;
                await report.WriteLineAsync(JsonSerializer.Serialize(plan)).ConfigureAwait(false);
                continue;
            }

            try
            {
                foreach (var itemId in plan.ItemIds)
                {
                    await manager.EnsurePermalinkIdsAsync(byId[itemId], cancellationToken).ConfigureAwait(false);
                    var binding = await repository.ReadForItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                    await repository.ImportAliasAsync(
                        new MediaPermalinkRepository.PermalinkBindingRequest(
                            itemId,
                            binding.ContentId,
                            binding.ContentId!.Value,
                            binding.Kind!,
                            [plan.Alias],
                            null,
                            clock.GetUtcNow().UtcDateTime),
                        cancellationToken).ConfigureAwait(false);
                }

                imported++;
            }
            catch (PermalinkException exception)
            {
                casualties++;
                logger.LogWarning(exception, "Legacy alias {Alias} could not be imported: {Code}", plan.Alias, exception.Code);
                await report.WriteLineAsync(JsonSerializer.Serialize(plan with { Error = $"{exception.Code}: {exception.Message}" })).ConfigureAwait(false);
            }
        }

        logger.LogInformation("Imported {Imported} of {Total} legacy aliases; {Casualties} aliases need repair. Report: {ReportPath}", imported, aliases.Count, casualties, reportPath);
        await report.WriteLineAsync(JsonSerializer.Serialize(new { Status = "summary", IssuedAliases = aliases.Count, ImportedAliases = imported, UnmappedAliases = casualties, MissingLibraryFiles = snapshot.Count(item => item.MissingFile) })).ConfigureAwait(false);
    }

    /// <summary>Builds the same read-only import plan for startup and an operator dry run.</summary>
    /// <param name="aliases">Issued legacy aliases read from the rollback database.</param>
    /// <param name="items">The current ordinary library metadata snapshot.</param>
    /// <returns>A plan for each issued alias, with explicit casualties instead of invented identity.</returns>
    public static IReadOnlyList<AliasPlan> Plan(IReadOnlyList<LegacyMediaPermalinkRepository.LegacyAlias> aliases, IReadOnlyList<LibraryRecord> items)
    {
        var result = new List<AliasPlan>(aliases.Count);
        foreach (var alias in aliases)
        {
            if (alias.Error is not null || !MediaContentIdentifier.IsPublicAlias(alias.Alias))
            {
                result.Add(new AliasPlan(alias.Alias, [], alias.Error ?? "legacy-alias-invalid"));
                continue;
            }

            var providerAlias = MediaContentIdentifier.ParseProviderAlias(alias.Alias);
            var matches = items.Where(item => !item.MissingFile
                && (alias.Kinds.Count == 0 || alias.Kinds.Any(kind => MediaContentIdentifier.IsKindRefinement(kind, item.Kind)
                    || MediaContentIdentifier.IsKindRefinement(item.Kind, kind)))
                && (providerAlias is not null
                    ? item.PublicAliases.Contains(alias.Alias, StringComparer.Ordinal)
                    : (alias.ItemIds.Contains(item.ItemId) || (item.Path is not null && alias.RecordedPaths.Contains(item.Path))
                        || item.PublicAliases.Intersect(alias.ProviderAliases, StringComparer.Ordinal).Any())
                        && (alias.ProviderAliases.Count == 0 || item.PublicAliases.Intersect(alias.ProviderAliases, StringComparer.Ordinal).Any())))
                .Select(item => item.ItemId).Distinct().ToArray();
            result.Add(new AliasPlan(alias.Alias, matches, matches.Length == 0 ? "no-current-library-title" : null));
        }

        return result;
    }

    /// <summary>Current library metadata used to plan the one-time migration.</summary>
    /// <param name="ItemId">The ordinary library record ID.</param>
    /// <param name="Kind">The logical content kind.</param>
    /// <param name="PublicAliases">Identifiers produced by MediaContentIdentifier.</param>
    /// <param name="Path">The old record locator, used only during migration.</param>
    /// <param name="MissingFile">A missing-file diagnostic, separate from identity.</param>
    public sealed record LibraryRecord(Guid ItemId, string Kind, IReadOnlyList<string> PublicAliases, string? Path, bool MissingFile);

    /// <summary>One issued alias and its current logical library records.</summary>
    /// <param name="Alias">The existing public URL identifier.</param>
    /// <param name="ItemIds">Current metadata matches.</param>
    /// <param name="Error">Why an alias cannot be imported, when applicable.</param>
    public sealed record AliasPlan(string Alias, IReadOnlyList<Guid> ItemIds, string? Error);
}
