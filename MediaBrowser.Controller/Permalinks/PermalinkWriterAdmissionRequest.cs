// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Requests a short, single-use filesystem writer admission; wire names are camelCase like the response.</summary>
/// <param name="RootIdentityId">The registered root identity.</param>
/// <param name="NormalizedRelativePath">The canonical path relative to that root.</param>
/// <param name="PinnedObjectIdentity">The filesystem object identity the writer pinned.</param>
/// <param name="Action">The admitted action.</param>
/// <param name="SnapshotGeneration">The latest published snapshot generation the writer read.</param>
public sealed record PermalinkWriterAdmissionRequest(
    [property: JsonPropertyName("rootIdentityId")] string RootIdentityId,
    [property: JsonPropertyName("normalizedRelativePath")] string NormalizedRelativePath,
    [property: JsonPropertyName("pinnedObjectIdentity")] string PinnedObjectIdentity,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("snapshotGeneration")] long SnapshotGeneration);
