using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Exposes metadata-free discovery and purpose-bound candidate redemption.
/// </summary>
[Route("Permalinks")]
[Authorize]
public sealed class PermalinkResolutionController : BaseJellyfinApiController
{
    private readonly IPermalinkResolutionService _resolution;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkResolutionController"/> class.
    /// </summary>
    /// <param name="resolution">The permalink resolution service.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="dtoService">The item DTO service.</param>
    public PermalinkResolutionController(
        IPermalinkResolutionService resolution,
        IUserManager userManager,
        IDtoService dtoService)
    {
        _resolution = resolution;
        _userManager = userManager;
        _dtoService = dtoService;
    }

    /// <summary>Discovers verified opaque candidates for one alias.</summary>
    /// <param name="permalinkId">The opaque permalink identifier.</param>
    /// <param name="purpose">The requested redemption purpose.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The ordered metadata-free candidate envelopes.</returns>
    [HttpGet("{permalinkId}/Items")]
    public async Task<ActionResult<QueryResult<PermalinkCandidateEnvelope>>> Discover(
        [FromRoute] string permalinkId,
        [FromQuery] string purpose = "details",
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async userId =>
        {
            var items = await _resolution.DiscoverAsync(
                permalinkId,
                purpose,
                userId,
                cancellationToken).ConfigureAwait(false);
            return Ok(new QueryResult<PermalinkCandidateEnvelope>(items));
        }).ConfigureAwait(false);
    }

    /// <summary>Consumes a details lease and returns the revalidated item DTO.</summary>
    /// <param name="handle">The opaque candidate handle.</param>
    /// <param name="request">The purpose-bound lease.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The revalidated item DTO.</returns>
    [HttpPost("Candidates/{handle}/Details")]
    public async Task<ActionResult<BaseItemDto>> Details(
        [FromRoute] string handle,
        [FromBody] PermalinkLeaseRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(async userId =>
        {
            var user = User.GetRequestUser(_userManager)!;
            var item = await _resolution.RedeemDetailsAsync(
                handle,
                request.Lease,
                userId,
                cancellationToken).ConfigureAwait(false);
            return Ok(_dtoService.GetBaseItemDto(item, new DtoOptions(), user));
        }).ConfigureAwait(false);
    }

    /// <summary>Exchanges one consumed details lease for a freshly checked playback lease.</summary>
    /// <param name="handle">The opaque candidate handle.</param>
    /// <param name="request">The details lease.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The playback-purpose candidate envelope.</returns>
    [HttpPost("Candidates/{handle}/PlaybackLease")]
    public async Task<ActionResult<PermalinkCandidateEnvelope>> PlaybackLease(
        [FromRoute] string handle,
        [FromBody] PermalinkLeaseRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(async userId => Ok(
            await _resolution.ExchangePlaybackLeaseAsync(
                handle,
                request.Lease,
                userId,
                cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>Consumes a playback lease and returns its immutable snapshot plan.</summary>
    /// <param name="handle">The opaque candidate handle.</param>
    /// <param name="request">The purpose-bound lease.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The immutable playback snapshot.</returns>
    [HttpPost("Candidates/{handle}/Playback")]
    public async Task<ActionResult<PermalinkPlaybackSnapshot>> Playback(
        [FromRoute] string handle,
        [FromBody] PermalinkLeaseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PlaybackSessionId))
        {
            return Problem(
                detail: "PlaybackSessionId is required when consuming a playback lease.",
                statusCode: StatusCodes.Status409Conflict,
                title: "playback-session-required");
        }

        return await ExecuteAsync(async userId => Ok(
            await _resolution.RedeemPlaybackAsync(
                handle,
                request.Lease,
                request.PlaybackSessionId,
                request.QueueOrdinal,
                request.Complete,
                userId,
                cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Guid, Task<ActionResult<T>>> action)
    {
        // Leases are bound to a user, so a token without one (an API key) has
        // nothing to bind to and is refused here rather than deeper in.
        if (User.GetRequestUser(_userManager) is null)
        {
            return Unauthorized();
        }

        try
        {
            return await action(User.GetUserId()).ConfigureAwait(false);
        }
        catch (PermalinkException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: exception.Kind == PermalinkErrorKind.Unavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status409Conflict,
                title: exception.Code);
        }
    }
}
