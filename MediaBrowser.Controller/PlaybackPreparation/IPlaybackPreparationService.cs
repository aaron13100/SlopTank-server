// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
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

    Task InvalidateReadinessIfChangedAsync(System.Collections.Generic.IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

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
