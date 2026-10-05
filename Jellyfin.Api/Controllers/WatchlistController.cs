// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Watchlist;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Per-user watchlist ("My List") controller.
/// </summary>
[Route("")]
[Authorize]
[Tags("Watchlist")]
public class WatchlistController : BaseJellyfinApiController
{
    private readonly IWatchlistManager _watchlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistController"/> class.
    /// </summary>
    /// <param name="watchlistManager">Instance of the <see cref="IWatchlistManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    public WatchlistController(
        IWatchlistManager watchlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService)
    {
        _watchlistManager = watchlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
    }

    /// <summary>
    /// Adds an item to the user's watchlist. Adding an item that is already on
    /// the list succeeds without changing it (idempotent).
    /// </summary>
    /// <param name="userId">The user id. Must be the authenticated user's own id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">The item is on the user's watchlist.</response>
    /// <response code="403">The route user is not the authenticated user.</response>
    /// <response code="404">The item does not exist or is not visible to the user.</response>
    /// <returns>A <see cref="NoContentResult"/> when the item is on the list.</returns>
    [HttpPost("Users/{userId}/Items/{itemId}/Watchlist")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AddToWatchlist(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
    {
        if (!IsOwnUser(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A watchlist is only reachable by its owner.");
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        await _watchlistManager.AddToWatchlistAsync(userId, itemId).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes an item from the user's watchlist. Removing an item that is not
    /// on the list succeeds without changing anything (idempotent).
    /// </summary>
    /// <param name="userId">The user id. Must be the authenticated user's own id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">The item is no longer on the user's watchlist.</response>
    /// <response code="403">The route user is not the authenticated user.</response>
    /// <returns>A <see cref="NoContentResult"/> when the item is off the list.</returns>
    [HttpDelete("Users/{userId}/Items/{itemId}/Watchlist")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> RemoveFromWatchlist(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
    {
        if (!IsOwnUser(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A watchlist is only reachable by its owner.");
        }

        await _watchlistManager.RemoveFromWatchlistAsync(userId, itemId).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the user's watchlist, newest-added first unless another order is
    /// requested. Entries whose item has left the library are omitted but
    /// preserved, and reappear automatically once the item is restored.
    /// </summary>
    /// <param name="userId">The user id. Must be the authenticated user's own id.</param>
    /// <param name="startIndex">The zero-based index of the first entry to return.</param>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="sortOrder">The sort order of the addition timestamp.</param>
    /// <response code="200">The watchlist page.</response>
    /// <response code="403">The route user is not the authenticated user.</response>
    /// <returns>A paged <see cref="QueryResult{T}"/> of item dtos.</returns>
    [HttpGet("Users/{userId}/Items/Watchlist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetWatchlist(
        [FromRoute, Required] Guid userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] SortOrder sortOrder = SortOrder.Descending)
    {
        if (!IsOwnUser(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A watchlist is only reachable by its owner.");
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var result = await _watchlistManager
            .GetWatchlistAsync(user, startIndex, limit, sortOrder)
            .ConfigureAwait(false);
        var dtoOptions = new DtoOptions();
        var dtos = result.Items
            .Select(item => _dtoService.GetBaseItemDto(item, dtoOptions, user))
            .ToArray();
        return new QueryResult<BaseItemDto>(result.StartIndex, result.TotalRecordCount, dtos);
    }

    /// <summary>
    /// Checks whether an item is currently on the user's watchlist.
    /// </summary>
    /// <param name="userId">The user id. Must be the authenticated user's own id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The membership answer.</response>
    /// <response code="403">The route user is not the authenticated user.</response>
    /// <returns>The membership state of the item.</returns>
    [HttpGet("Users/{userId}/Items/Watchlist/Contains")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WatchlistContainsResponse>> ContainsInWatchlist(
        [FromRoute, Required] Guid userId,
        [FromQuery, Required] Guid itemId)
    {
        if (!IsOwnUser(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A watchlist is only reachable by its owner.");
        }

        var member = await _watchlistManager.IsInWatchlistAsync(userId, itemId).ConfigureAwait(false);
        return new WatchlistContainsResponse(itemId, member);
    }

    private bool IsOwnUser(Guid userId)
    {
        return userId.Equals(User.GetUserId());
    }
}
