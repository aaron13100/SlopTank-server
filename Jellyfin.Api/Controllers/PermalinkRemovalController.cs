// SlopTank modification notice: added or changed by SlopTank on 2026-09-27, 2026-10-01.
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
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Api.Controllers;

/// <summary>Exposes the narrow administrator-only durable removal boundary.</summary>
[Route("Permalinks/Removal")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class PermalinkRemovalController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPermalinkRemovalCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>Initializes a new instance of the <see cref="PermalinkRemovalController"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="coordinator">The durable mutation coordinator.</param>
    /// <param name="lifetime">The host lifetime that bounds journaled durable work.</param>
    public PermalinkRemovalController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPermalinkRemovalCoordinator coordinator,
        IHostApplicationLifetime lifetime)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _coordinator = coordinator;
        _lifetime = lifetime;
    }

    // Journaled work is bound to the host lifetime, never to RequestAborted (c196).
    private CancellationToken DurableToken => _lifetime.ApplicationStopping;

    /// <summary>Freezes exact removal intent before external mutation.</summary>
    /// <param name="request">The immutable removal intent.</param>
    /// <returns>The folded prepared removal state.</returns>
    [HttpPost("Prepare")]
    public async Task<ActionResult<PermalinkRemovalResult>> Prepare(
        [FromBody] PermalinkRemovalPrepareRequest request)
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
            () => _coordinator.PrepareAsync(item, request, DurableToken))
            .ConfigureAwait(false);
    }

    /// <summary>Publishes one exact prepared removal after its file move.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="request">The exact post-move confirmation.</param>
    /// <returns>The folded committed removal state.</returns>
    [HttpPost("{operationId}/Commit")]
    public Task<ActionResult<PermalinkRemovalResult>> Commit(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkRemovalCommitRequest request)
    {
        return ExecuteAsync(
            () => _coordinator.CommitAsync(operationId, request, DurableToken));
    }

    /// <summary>Reconciles one interrupted prepared removal.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The folded recovered removal state.</returns>
    [HttpPost("{operationId}/Recover")]
    public Task<ActionResult<PermalinkRemovalResult>> Recover(
        [FromRoute] Guid operationId)
    {
        return ExecuteAsync(
            () => _coordinator.RecoverAsync(operationId, DurableToken));
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
