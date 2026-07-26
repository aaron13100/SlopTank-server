using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Discovers opaque permalink candidates and redeems purpose-bound leases.
/// </summary>
public interface IPermalinkResolutionService
{
    /// <summary>Returns metadata-free verified candidate envelopes.</summary>
    Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>Consumes a details lease after repeating all evidence checks.</summary>
    Task<BaseItem> RedeemDetailsAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>Consumes a playback lease and materializes an immutable snapshot plan.</summary>
    Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken);
}

/// <summary>Metadata-free candidate returned by discovery.</summary>
public sealed record PermalinkCandidateEnvelope(int Rank, string Namespace, string Handle, string Lease);

/// <summary>Opaque lease body accepted by candidate redemption.</summary>
public sealed record PermalinkLeaseRequest(string Lease);

/// <summary>Verified immutable playback result.</summary>
public sealed record PermalinkPlaybackSnapshot(Guid ItemId, string SnapshotPath, int QueueCount);
