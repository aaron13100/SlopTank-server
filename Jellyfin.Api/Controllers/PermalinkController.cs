// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-31, 2026-09-09, 2026-09-19, 2026-10-14, 2026-10-31, 2026-11-04, 2026-11-13.
using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
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
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="permalinkManager">The durable permalink manager.</param>
    /// <param name="userManager">The user manager.</param>
    public PermalinkController(
        ILibraryManager libraryManager,
        IPermalinkManager permalinkManager,
        IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _permalinkManager = permalinkManager;
        _userManager = userManager;
    }

    /// <summary>Reads an existing title's indexing state and recorded removal origin.</summary>
    /// <param name="identifier">The original public title alias.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The versioned administrative removal record.</returns>
    [HttpGet("Permalinks/{identifier}/Removal")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<PermalinkRemovalResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<PermalinkRemovalResponse>> GetRemoval(
        [FromRoute, Required] string identifier,
        CancellationToken cancellationToken)
        => RemovalResultAsync(_permalinkManager.GetRemovalAsync(identifier, cancellationToken));

    /// <summary>Confirms a known deliberate historical deletion of an already-absent title.</summary>
    /// <remarks>Does not delete files, mint aliases, rebind identity, or mark indexed titles removed.</remarks>
    /// <param name="identifier">The original public title alias.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The versioned record, retaining its timestamp on repeated confirmation.</returns>
    [HttpPost("Permalinks/{identifier}/Removal")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType<PermalinkRemovalResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<PermalinkRemovalResponse>> ConfirmRemoval(
        [FromRoute, Required] string identifier,
        CancellationToken cancellationToken)
        => RemovalResultAsync(_permalinkManager.ConfirmRemovalAsync(identifier, cancellationToken));

    private async Task<ActionResult<PermalinkRemovalResponse>> RemovalResultAsync(Task<PermalinkRemovalResponse?> operation)
    {
        try
        {
            var value = await operation.ConfigureAwait(false);
            return value is null ? NotFound() : Ok(value);
        }
        catch (PermalinkException exception)
        {
            return PermalinkProblem(exception);
        }
    }

    private ObjectResult PermalinkProblem(PermalinkException exception)
    {
        var status = exception.Kind switch
        {
            PermalinkErrorKind.Ineligible => StatusCodes.Status400BadRequest,
            PermalinkErrorKind.Conflict => StatusCodes.Status409Conflict,
            PermalinkErrorKind.Corrupt => StatusCodes.Status503ServiceUnavailable,
            PermalinkErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        return Problem(detail: exception.Message, statusCode: status, title: exception.Code);
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
        var user = User.GetRequestUser(_userManager);
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
            return PermalinkProblem(exception);
        }
    }
}
