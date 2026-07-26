using System;
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
/// Exposes the authenticated durable mutation journal boundary.
/// </summary>
[Route("Permalinks/Rewrite")]
[Authorize]
public sealed class PermalinkRewriteController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPermalinkMutationCoordinator _coordinator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkRewriteController"/> class.
    /// </summary>
    public PermalinkRewriteController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPermalinkMutationCoordinator coordinator)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _coordinator = coordinator;
    }

    /// <summary>Freezes the current verified state before mutation.</summary>
    [HttpPost("Prepare")]
    public async Task<ActionResult<PermalinkMutationResult>> Prepare(
        [FromBody] PermalinkMutationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(User.GetUserId());
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

    /// <summary>Commits one exact prepared mutation.</summary>
    [HttpPost("{operationId}/Commit")]
    public Task<ActionResult<PermalinkMutationResult>> Commit(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _coordinator.CommitAsync(operationId, request, cancellationToken));
    }

    /// <summary>Recovers one interrupted prepared mutation.</summary>
    [HttpPost("{operationId}/Recover")]
    public Task<ActionResult<PermalinkMutationResult>> Recover(
        [FromRoute] Guid operationId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _coordinator.RecoverAsync(operationId, cancellationToken));
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
