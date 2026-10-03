// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>The complete hot/archive operation journal generations used by an absence watermark.</summary>
/// <param name="HotGeneration">The digest of every hot journal file.</param>
/// <param name="HotCardinality">The number of hot operation identifiers.</param>
/// <param name="ArchiveGeneration">The digest of every archived journal file and month assignment.</param>
/// <param name="ArchiveCardinality">The number of archived operation identifiers.</param>
internal sealed record PermalinkJournalCensus(
    string HotGeneration,
    int HotCardinality,
    string ArchiveGeneration,
    int ArchiveCardinality)
{
    /// <summary>Gets the exact number of distinct operation identifiers.</summary>
    public int TotalCardinality => checked(HotCardinality + ArchiveCardinality);
}
