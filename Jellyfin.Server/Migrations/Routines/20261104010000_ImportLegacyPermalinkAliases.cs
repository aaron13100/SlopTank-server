// SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Permalinks;
using Jellyfin.Server.Migrations.Stages;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>Preserves issued URLs in the application's logical alias table.</summary>
/// <param name="importer">The read-only legacy importer.</param>
[JellyfinMigration("2026-11-04T01:00:00", nameof(ImportLegacyPermalinkAliases), Stage = JellyfinMigrationStageTypes.AppInitialisation)]
[JellyfinMigrationBackup(JellyfinDb = true)]
public sealed class ImportLegacyPermalinkAliases(LegacyPermalinkImport importer) : IAsyncMigrationRoutine
{
    /// <inheritdoc />
    public Task PerformAsync(CancellationToken cancellationToken) => importer.RunAsync(cancellationToken);
}
