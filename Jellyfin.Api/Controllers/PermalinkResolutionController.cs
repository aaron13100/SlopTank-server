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

    /// <summary>Initializes a new instance of the controller.</summary>
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
    [HttpPost("Candidates/{handle}/Details")]
    public async Task<ActionResult<BaseItemDto>> Details(
        [FromRoute] string handle,
        [FromBody] PermalinkLeaseRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(async userId =>
        {
            var user = _userManager.GetUserById(userId)!;
            var item = await _resolution.RedeemDetailsAsync(
                handle,
                request.Lease,
                userId,
                cancellationToken).ConfigureAwait(false);
            return Ok(_dtoService.GetBaseItemDto(item, new DtoOptions(), user));
        }).ConfigureAwait(false);
    }

    /// <summary>Consumes a playback lease and returns its immutable snapshot plan.</summary>
    [HttpPost("Candidates/{handle}/Playback")]
    public async Task<ActionResult<PermalinkPlaybackSnapshot>> Playback(
        [FromRoute] string handle,
        [FromBody] PermalinkLeaseRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(async userId => Ok(
            await _resolution.RedeemPlaybackAsync(
                handle,
                request.Lease,
                userId,
                cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Guid, Task<ActionResult<T>>> action)
    {
        var userId = User.GetUserId();
        if (_userManager.GetUserById(userId) is null)
        {
            return Unauthorized();
        }

        try
        {
            return await action(userId).ConfigureAwait(false);
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
