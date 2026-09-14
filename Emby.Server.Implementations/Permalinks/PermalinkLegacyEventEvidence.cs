// SlopTank modification notice: added or changed by SlopTank on 2026-09-14.
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>One capsule event read from the store with the digest of its exact stored bytes.</summary>
/// <param name="Event">The deserialized event document.</param>
/// <param name="StoredDigest">The canonical digest of the exact bytes found in the store.</param>
internal sealed record PermalinkLegacyEventEvidence(PermalinkEventDocument Event, string StoredDigest);
