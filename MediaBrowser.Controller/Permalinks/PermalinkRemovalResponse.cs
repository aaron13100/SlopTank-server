// SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Describes recorded removal provenance without changing a title's identifiers.</summary>
/// <param name="Version">The response format version.</param>
/// <param name="Identifier">The existing public alias.</param>
/// <param name="ContentId">The alias's logical title identity.</param>
/// <param name="LibraryItemCount">The current indexed records, not a physical-playability claim.</param>
/// <param name="Status">Indexed, IntentionallyDeleted, Removed, or Unrecorded.</param>
/// <param name="Cause">The last recorded origin, including unknown future values.</param>
/// <param name="RecordedAtUtc">When the removal or historical confirmation was recorded.</param>
public sealed record PermalinkRemovalResponse(
    int Version,
    string Identifier,
    Guid ContentId,
    int LibraryItemCount,
    string Status,
    string? Cause,
    DateTime? RecordedAtUtc);
