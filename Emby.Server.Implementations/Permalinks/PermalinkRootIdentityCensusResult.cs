// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System.Collections.Generic;
using System.Text.Json;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>The canonical root-identity generation set after fresh marker and object validation.</summary>
/// <param name="RootGenerationsJson">The ordered identity-to-generation map.</param>
internal sealed record PermalinkRootIdentityCensusResult(string RootGenerationsJson)
{
    /// <summary>Gets the validated roots by stable identity.</summary>
    public IReadOnlyDictionary<string, long> RootGenerations
        => JsonSerializer.Deserialize<SortedDictionary<string, long>>(RootGenerationsJson)
            ?? new SortedDictionary<string, long>();
}
