// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System.Collections.Generic;
using System.Text.Json;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>The exact P6 absence boundary sealed after a complete registry population.</summary>
/// <param name="WatermarkId">The immutable seal identifier.</param>
/// <param name="RootGenerationsJson">The canonical ordered root-identity generation map.</param>
/// <param name="Journal">The complete hot and archive journal census.</param>
/// <param name="RegistryWriterVersion">The registry population contract version.</param>
/// <param name="ContentEpoch">The authority content epoch.</param>
/// <param name="JournalCounter">The authority journal publication counter.</param>
/// <param name="RegistryCardinality">The exact number of registry evidence rows.</param>
/// <param name="PopulationId">The complete population run this seal binds.</param>
/// <param name="CreatedAt">The canonical UTC timestamp.</param>
internal sealed record PermalinkAbsenceWatermark(
    System.Guid WatermarkId,
    string RootGenerationsJson,
    PermalinkJournalCensus Journal,
    int RegistryWriterVersion,
    long ContentEpoch,
    long JournalCounter,
    long RegistryCardinality,
    string PopulationId,
    string CreatedAt)
{
    /// <summary>Gets the bound root generations by stable identity.</summary>
    public IReadOnlyDictionary<string, long> RootGenerations
        => JsonSerializer.Deserialize<SortedDictionary<string, long>>(RootGenerationsJson)
            ?? new SortedDictionary<string, long>();
}
