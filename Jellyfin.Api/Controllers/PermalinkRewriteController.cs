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
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="coordinator">The durable mutation coordinator.</param>
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
    /// <param name="request">The immutable mutation intent.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded prepared operation state.</returns>
    [HttpPost("Prepare")]
    public async Task<ActionResult<PermalinkMutationResult>> Prepare(
        [FromBody] PermalinkMutationPrepareRequest request,
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

    /// <summary>Commits one exact prepared mutation.</summary>
    /// <param name="operationId">The prepared operation identifier.</param>
    /// <param name="request">The exact commit input.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded committed or suspended operation state.</returns>
    [HttpPost("{operationId}/Commit")]
    public Task<ActionResult<PermalinkMutationResult>> Commit(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkMutationCommitRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _coordinator.CommitAsync(operationId, request, cancellationToken));
    }

    /// <summary>Recovers one interrupted prepared mutation.</summary>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded recovered operation state.</returns>
    [HttpPost("{operationId}/Recover")]
    public Task<ActionResult<PermalinkMutationResult>> Recover(
        [FromRoute] Guid operationId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _coordinator.RecoverAsync(operationId, cancellationToken));
    }

    /// <summary>Terminalizes a suspended mutation without asserting unknown published state.</summary>
    /// <param name="operationId">The suspended operation identifier.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded terminal operation state.</returns>
    [HttpPost("{operationId}/Abort")]
    public Task<ActionResult<PermalinkMutationResult>> Abort(
        [FromRoute] Guid operationId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _coordinator.AbortAsync(operationId, cancellationToken));
    }

    /// <summary>Continues a suspended operation only when exact evidence permits it.</summary>
    /// <param name="operationId">The suspended operation identifier.</param>
    /// <param name="request">The guarded administrator action.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The folded continued operation state.</returns>
    [HttpPost("{operationId}/ResolveOperation")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<PermalinkMutationResult>> ResolveOperation(
        [FromRoute] Guid operationId,
        [FromBody] PermalinkOperationResolutionRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            () => _coordinator.ResolveAsync(operationId, request, cancellationToken));
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
