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
    /// <param name="permalinkId">The opaque permalink identifier.</param>
    /// <param name="purpose">The requested redemption purpose.</param>
    /// <param name="userId">The authenticated user identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ordered opaque candidate envelopes.</returns>
    Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>Consumes a details lease after repeating all evidence checks.</summary>
    /// <param name="handle">The opaque candidate handle.</param>
    /// <param name="lease">The purpose-bound lease.</param>
    /// <param name="userId">The authenticated user identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The verified local item.</returns>
    Task<BaseItem> RedeemDetailsAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>Consumes a playback lease and materializes an immutable snapshot plan.</summary>
    /// <param name="handle">The opaque candidate handle.</param>
    /// <param name="lease">The purpose-bound lease.</param>
    /// <param name="userId">The authenticated user identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The immutable playback snapshot.</returns>
    Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken);
}
