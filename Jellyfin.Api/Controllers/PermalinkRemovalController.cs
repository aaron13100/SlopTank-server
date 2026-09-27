// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
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

/// <summary>Exposes the narrow administrator-only durable removal boundary.</summary>
[Route("Permalinks/Removal")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class PermalinkRemovalController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPermalinkRemovalCoordinator _coordinator;

    /// <summary>Initializes a new instance of the <see cref="PermalinkRemovalController"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="coordinator">The durable mutation coordinator.</param>
    public PermalinkRemovalController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPermalinkRemovalCoordinator coordinator)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _coordinator = coordinator;
    }

    /// <summary>Freezes exact removal intent before external mutation.</summary>
    /// <param name="request">The immutable removal intent.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded prepared removal state.</returns>
    [HttpPost("Prepare")]
    public async Task<ActionResult<PermalinkRemovalResult>> Prepare(
        [FromBody] PermalinkRemovalPrepareRequest request,
        CancellationToken cancellationToken)
    {
        var user = User.GetRequestUser(_userManager);
        var item = user is null
            ? null
            : _libraryManager.GetItemById<BaseItem>(request.ItemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return await ExecuteAsync(
            () => _coordinator.PrepareAsync(item, request, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Publishes one exact prepared removal after its file move.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="request">The exact post-move confirmation.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded committed removal state.</returns>
    [HttpPost("{operationId}/Commit")]
    public Task<ActionResult<PermalinkRemovalResult>> Commit(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkRemovalCommitRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            () => _coordinator.CommitAsync(operationId, request, cancellationToken));
    }

    /// <summary>Reconciles one interrupted prepared removal.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded recovered removal state.</returns>
    [HttpPost("{operationId}/Recover")]
    public Task<ActionResult<PermalinkRemovalResult>> Recover(
        [FromRoute] Guid operationId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            () => _coordinator.RecoverAsync(operationId, cancellationToken));
    }

    private async Task<ActionResult<PermalinkRemovalResult>> ExecuteAsync(
        Func<Task<PermalinkRemovalResult>> action)
    {
        try
        {
            return Ok(await action().ConfigureAwait(false));
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
