// SlopTank modification notice: added or changed by SlopTank on 2026-10-16.
using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Api.Extensions;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Playback progress with a server-owned revision, and conditional, idempotent offline progress
/// operations (c203). The legacy user data endpoints are unchanged.
/// </summary>
[Route("")]
[Authorize]
[Tags("PlaybackProgress")]
public class PlaybackProgressController : BaseJellyfinApiController
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackProgressController"/> class.
    /// </summary>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    public PlaybackProgressController(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _appHost = appHost;
    }

    /// <summary>
    /// Gets the authenticated user's playback progress of an item together with its playback revision.
    /// An offline client stores this as its baseline before it goes offline.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The playback progress state.</response>
    /// <response code="403">The request is not made by a user.</response>
    /// <response code="404">The item does not exist or is not visible to the user.</response>
    /// <returns>The <see cref="PlaybackProgressStateDto"/>.</returns>
    [HttpGet("UserItems/{itemId}/PlaybackProgress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PlaybackProgressStateDto> GetPlaybackProgress([FromRoute, Required] Guid itemId)
    {
        var (user, error) = ResolveUser();
        if (user is null)
        {
            return error!;
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var state = _userDataManager.GetPlaybackProgressState(user, item);
        state.ServerId = _appHost.SystemId;
        return state;
    }

    /// <summary>
    /// Applies an offline playback progress operation if, and only if, the item's playback revision still
    /// equals the operation's baseline revision. An identical retry returns the recorded outcome without
    /// writing again.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="operation">The operation.</param>
    /// <response code="200">Applied (or a replay of an applied operation).</response>
    /// <response code="400">The operation is malformed or bound to another device or server.</response>
    /// <response code="403">The request is not made by a user.</response>
    /// <response code="404">The item does not exist or is not visible to the user.</response>
    /// <response code="409">The server playback state changed after the baseline; nothing was written.</response>
    /// <response code="410">The operation's sequence is older than the retained receipts; nothing was written.</response>
    /// <response code="422">The operation id or sequence was used with a different payload; nothing was written.</response>
    /// <response code="507">The user has too many devices holding receipts; nothing was written.</response>
    /// <returns>The <see cref="PlaybackProgressOperationResultDto"/>.</returns>
    [HttpPost("UserItems/{itemId}/PlaybackProgress/Operations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status507InsufficientStorage)]
    public ActionResult<PlaybackProgressOperationResultDto> ApplyPlaybackProgressOperation(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] PlaybackProgressOperationDto operation)
    {
        var (user, error) = ResolveUser();
        if (user is null)
        {
            return error!;
        }

        var deviceId = User.GetDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId) || !string.Equals(deviceId, operation.DeviceId, StringComparison.Ordinal))
        {
            return BadRequest("The operation's DeviceId does not match the authenticated device.");
        }

        if (!string.Equals(operation.ServerId, _appHost.SystemId, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("The operation's ServerId does not match this server.");
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        PlaybackProgressOperationResultDto result;
        try
        {
            result = _userDataManager.ApplyPlaybackProgressOperation(user, item, deviceId, operation);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        if (result.State is not null)
        {
            result.State.ServerId = _appHost.SystemId;
        }

        var status = result.Outcome switch
        {
            PlaybackProgressOperationOutcome.Applied => StatusCodes.Status200OK,
            PlaybackProgressOperationOutcome.Conflict => StatusCodes.Status409Conflict,
            PlaybackProgressOperationOutcome.Expired => StatusCodes.Status410Gone,
            PlaybackProgressOperationOutcome.PayloadMismatch => StatusCodes.Status422UnprocessableEntity,
            PlaybackProgressOperationOutcome.CapacityExceeded => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status500InternalServerError
        };
        return StatusCode(status, result);
    }

    private (User? User, ActionResult? Error) ResolveUser()
    {
        var userId = User.GetUserId();
        if (userId.Equals(Guid.Empty) || User.GetIsApiKey())
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden, "Playback progress operations require a user session."));
        }

        var user = _userManager.GetUserById(userId);
        return user is null ? (null, NotFound()) : (user, null);
    }
}
