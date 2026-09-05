namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The durable resume state for the one-time pending-set migration, and the running total it keeps
/// afterward for boot-time reporting.
/// </summary>
/// <param name="LastOperationId">
/// The ordinal-sorted operation directory name the migration has verified up to, or null before it
/// has verified anything.
/// </param>
/// <param name="DirectoriesSeen">
/// The durable count of operation directories ever created, seeded by the migration walk and
/// incremented by <see cref="PermalinkOperationJournal.WriteOperationAsync"/> afterward.
/// </param>
/// <param name="Completed">Whether the one-time migration has finished.</param>
internal sealed record PermalinkPendingMigrationState(
    string? LastOperationId,
    long DirectoriesSeen,
    bool Completed);
