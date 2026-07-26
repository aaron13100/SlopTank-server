using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Issues durable permalink aliases for visible local library items.
/// </summary>
[Route("")]
[Authorize]
public sealed class PermalinkController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPermalinkManager _permalinkManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkController"/> class.
    /// </summary>
    public PermalinkController(
        ILibraryManager libraryManager,
        IPermalinkManager permalinkManager,
        IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _permalinkManager = permalinkManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Ensures and returns the item's ordered durable permalink aliases.
    /// </summary>
    /// <param name="itemId">The visible item id.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The versioned permalink response.</returns>
    [HttpPost("Items/{itemId}/Permalink")]
    [ProducesResponseType<PermalinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PermalinkResponse>> EnsurePermalink(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        var item = user is null
            ? null
            : _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            return Ok(await _permalinkManager.EnsurePermalinkIdsAsync(
                item,
                cancellationToken).ConfigureAwait(false));
        }
        catch (PermalinkException exception)
        {
            var status = exception.Kind switch
            {
                PermalinkErrorKind.Ineligible => StatusCodes.Status400BadRequest,
                PermalinkErrorKind.Conflict => StatusCodes.Status409Conflict,
                PermalinkErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError
            };
            return Problem(
                detail: exception.Message,
                statusCode: status,
                title: exception.Code);
        }
    }
}
