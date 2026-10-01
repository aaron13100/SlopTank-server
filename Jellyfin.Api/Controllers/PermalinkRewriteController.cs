// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-31, 2026-09-08, 2026-09-09, 2026-09-14, 2026-10-01.
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

/// <summary>
/// Exposes the authenticated durable mutation journal boundary.
/// </summary>
[Route("Permalinks/Rewrite")]
[Authorize]
public sealed class PermalinkRewriteController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPermalinkMutationCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkRewriteController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="coordinator">The durable mutation coordinator.</param>
    /// <param name="lifetime">The host lifetime that bounds journaled durable work.</param>
    public PermalinkRewriteController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPermalinkMutationCoordinator coordinator,
        IHostApplicationLifetime lifetime)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _coordinator = coordinator;
        _lifetime = lifetime;
    }

    // Journaled work is bound to the host lifetime, never to RequestAborted: a client
    // that disconnects mid-copy must not cancel a durable operation (c196).
    private CancellationToken DurableToken => _lifetime.ApplicationStopping;

    /// <summary>Freezes the current verified state before mutation.</summary>
    /// <param name="request">The immutable mutation intent.</param>
    /// <returns>The folded prepared operation state.</returns>
    [HttpPost("Prepare")]
    public async Task<ActionResult<PermalinkMutationResult>> Prepare(
        [FromBody] PermalinkMutationPrepareRequest request)
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

    /// <summary>Commits one exact prepared mutation.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="request">The exact commit input.</param>
    /// <returns>The folded committed or suspended operation state.</returns>
    [HttpPost("{operationId}/Commit")]
    public Task<ActionResult<PermalinkMutationResult>> Commit(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkMutationCommitRequest request)
    {
        return ExecuteAsync(() => _coordinator.CommitAsync(operationId, request, DurableToken));
    }

    /// <summary>Recovers one interrupted prepared mutation.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <returns>The folded recovered operation state.</returns>
    [HttpPost("{operationId}/Recover")]
    public Task<ActionResult<PermalinkMutationResult>> Recover(
        [FromRoute] Guid operationId)
    {
        return ExecuteAsync(() => _coordinator.RecoverAsync(operationId, DurableToken));
    }

    /// <summary>Terminalizes a suspended mutation without asserting unknown published state.</summary>
    /// <param name="operationId">The suspended operation identifier.</param>
    /// <returns>The folded terminal operation state.</returns>
    [HttpPost("{operationId}/Abort")]
    public Task<ActionResult<PermalinkMutationResult>> Abort(
        [FromRoute] Guid operationId)
    {
        return ExecuteAsync(() => _coordinator.AbortAsync(operationId, DurableToken));
    }

    /// <summary>Continues a suspended operation only when exact evidence permits it.</summary>
    /// <param name="operationId">The suspended operation identifier.</param>
    /// <param name="request">The guarded administrator action.</param>
    /// <returns>The folded continued operation state.</returns>
    [HttpPost("{operationId}/ResolveOperation")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<PermalinkMutationResult>> ResolveOperation(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkOperationResolutionRequest request)
    {
        return ExecuteAsync(
            () => _coordinator.ResolveAsync(operationId, request, DurableToken));
    }

    /// <summary>Publishes an authorized successor for an already-fenced leaf after exact operator evidence.</summary>
    /// <param name="request">The administrator-confirmed adoption evidence.</param>
    /// <returns>The folded adopted operation state.</returns>
    [HttpPost("AdoptContent")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<PermalinkMutationResult>> AdoptContent(
        [FromBody] PermalinkContentAdoptionRequest request)
    {
        var user = User.GetRequestUser(_userManager);
        if (user is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById<BaseItem>(request.ItemId, user);
        if (item is null)
        {
            return NotFound();
        }

        // The elevation policy on the route runs before this lookup, and the
        // administrator identity is stamped here from the authenticated user rather
        // than accepted from the request body it arrived in.
        return await ExecuteAsync(
            () => _coordinator.AdoptContentAsync(
                item,
                request with { AdministratorUserId = user.Id },
                DurableToken))
            .ConfigureAwait(false);
    }

    private async Task<ActionResult<PermalinkMutationResult>> ExecuteAsync(
        Func<Task<PermalinkMutationResult>> action)
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
