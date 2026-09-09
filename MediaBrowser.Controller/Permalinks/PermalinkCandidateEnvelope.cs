// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Metadata-free candidate returned by discovery.</summary>
/// <param name="Rank">The deterministic candidate rank.</param>
/// <param name="Namespace">The opaque identifier namespace.</param>
/// <param name="Handle">The opaque candidate handle.</param>
/// <param name="Lease">The purpose-bound lease.</param>
public sealed record PermalinkCandidateEnvelope(int Rank, string Namespace, string Handle, string Lease);
