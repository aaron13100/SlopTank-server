// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-07-31, 2026-09-08, 2026-09-09, 2026-09-19, 2026-10-14, 2026-10-31, 2026-11-04.
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

/// <summary>Resolves public aliases using the ordinary authorized library item flow.</summary>
[Route("Permalinks")]
[Authorize]
public sealed class PermalinkResolutionController : BaseJellyfinApiController
{
    private readonly IPermalinkManager _permalinks;
    private readonly IUserManager _users;
    private readonly IDtoService _dtos;

    /// <summary>Initializes a new instance of the <see cref="PermalinkResolutionController"/> class.</summary>
    /// <param name="permalinks">The logical alias service.</param>
    /// <param name="users">The existing user service.</param>
    /// <param name="dtos">The existing library DTO service.</param>
    public PermalinkResolutionController(IPermalinkManager permalinks, IUserManager users, IDtoService dtos)
    {
        _permalinks = permalinks;
        _users = users;
        _dtos = dtos;
    }

    /// <summary>Gets the current visible library titles named by an existing permalink.</summary>
    /// <param name="permalinkId">The provider or opaque alias from the public URL.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>Ordinary item DTOs, ready for the existing details and playback APIs.</returns>
    [HttpGet("{permalinkId}/Items")]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> Discover(
        [FromRoute] string permalinkId,
        CancellationToken cancellationToken = default)
    {
        var user = User.GetRequestUser(_users);
        if (user is null)
        {
            return Unauthorized();
        }

        try
        {
            var items = await _permalinks.GetVideoFileAsync(permalinkId, user.Id, cancellationToken).ConfigureAwait(false);
            return Ok(new QueryResult<BaseItemDto>(_dtos.GetBaseItemDtos(items, new DtoOptions(), user)));
        }
        catch (PermalinkException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: exception.Kind is PermalinkErrorKind.Unavailable or PermalinkErrorKind.Corrupt
                    ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict,
                title: exception.Code);
        }
    }
}
