// SlopTank modification notice: added or changed by SlopTank on 2026-10-04, 2026-10-14, 2026-10-31, 2026-11-11.
#pragma warning disable CS1591
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.PlaybackPreparation;

namespace MediaBrowser.Controller.PlaybackPreparation;

/// <summary>Server-owned admission and result boundary for the worker.</summary>
public interface IPlaybackPreparationService
{
    Task<PlaybackPreparationRequest?> ClaimAsync(CancellationToken cancellationToken);

    Task SubmitResultAsync(Guid requestId, PlaybackPreparationResult result, CancellationToken cancellationToken);

    Task RenewAsync(Guid requestId, PlaybackPreparationRequest request, CancellationToken cancellationToken);

    Task<PlaybackPreparationRequest> RecheckAsync(Guid requestId, PlaybackPreparationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies that a Ready item still describes the file under its path. When it does not,
    /// the persisted row and <paramref name="item"/> itself return to Pending.
    /// </summary>
    /// <param name="item">The item about to be played.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when playback may proceed.</returns>
    Task<bool> VerifyPlaybackReadinessAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the items whose persisted Ready row no longer describes the file under the item's
    /// current path, so the caller can return them to Pending before saving.
    /// </summary>
    /// <param name="items">The items about to be saved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids of the items whose readiness must be invalidated.</returns>
    Task<System.Collections.Generic.IReadOnlyCollection<Guid>> FindChangedReadinessAsync(
        System.Collections.Generic.IReadOnlyCollection<BaseItem> items,
        CancellationToken cancellationToken);
}

public sealed class PlaybackPreparationException : Exception
{
    public PlaybackPreparationException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string Code { get; }
}
