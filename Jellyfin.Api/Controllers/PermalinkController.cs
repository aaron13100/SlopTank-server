// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-31, 2026-09-09.
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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
    private readonly IPermalinkIdentityMutationAdapter _identityMutationAdapter;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="permalinkManager">The durable permalink manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="identityMutationAdapter">The identity mutation adapter.</param>
    public PermalinkController(
        ILibraryManager libraryManager,
        IPermalinkManager permalinkManager,
        IUserManager userManager,
        IPermalinkIdentityMutationAdapter identityMutationAdapter)
    {
        _libraryManager = libraryManager;
        _permalinkManager = permalinkManager;
        _userManager = userManager;
        _identityMutationAdapter = identityMutationAdapter;
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

    /// <summary>Cancels the item's latest recoverable logical reassignment.</summary>
    /// <param name="itemId">The visible item id.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded cancelled or restored mutation state.</returns>
    [HttpPost("Items/{itemId}/Permalink/CancelLogicalReassignment")]
    [ProducesResponseType<PermalinkMutationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PermalinkMutationResult>> CancelLogicalReassignment(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            return Ok(await _identityMutationAdapter.CancelPendingAsync(
                item,
                cancellationToken).ConfigureAwait(false));
        }
        catch (PermalinkException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: exception.Code);
        }
    }

    /// <summary>Confirms an exact current provider assignment through the durable logical boundary.</summary>
    /// <param name="itemId">The visible item id.</param>
    /// <param name="request">The exact logical assignment confirmation.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A no-content response after durable confirmation.</returns>
    [HttpPost("Items/{itemId}/Permalink/LogicalAssignment")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> ConfirmLogicalAssignment(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] PermalinkLogicalAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.OperationId, out var operationId)
            || request.ProviderIds is null)
        {
            return BadRequest("A parseable operation id and provider assignment are required.");
        }

        var item = GetVisibleItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!ProviderIdsEqual(item.ProviderIds, request.ProviderIds))
        {
            return Conflict("Current provider assignment does not match the requested confirmation.");
        }

        try
        {
            _ = await _identityMutationAdapter.CommitPendingAsync(
                item,
                operationId,
                request.ProviderIds,
                cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (PermalinkException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: exception.Code);
        }
    }

    private BaseItem? GetVisibleItem(Guid itemId)
    {
        var user = User.GetRequestUser(_userManager);
        return user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
    }

    private static bool ProviderIdsEqual(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> desired)
    {
        return current.Count == desired.Count
            && current.All(pair => desired.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }
}
