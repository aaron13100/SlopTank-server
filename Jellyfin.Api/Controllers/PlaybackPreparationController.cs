// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
// allow-no-test-found: private integration coverage is assigned to sloptank-tests/server/tests.
#pragma warning disable CS1591
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

[ApiController]
[Route("PlaybackPreparation")]
[Authorize(Policy = Policies.PlaybackPreparationWorker)]
public sealed class PlaybackPreparationController : ControllerBase
{
    private readonly IPlaybackPreparationService _service;

    public PlaybackPreparationController(IPlaybackPreparationService service)
    {
        _service = service;
    }

    [HttpPost("Claim")]
    [ProducesResponseType(typeof(PlaybackPreparationRequest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<PlaybackPreparationRequest>> Claim(CancellationToken cancellationToken)
    {
        var request = await _service.ClaimAsync(cancellationToken).ConfigureAwait(false);
        return request is null ? NoContent() : Ok(request);
    }

    [HttpPost("{requestId:guid}/Result")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Result(Guid requestId, [FromBody] PlaybackPreparationResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _service.SubmitResultAsync(requestId, result, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (PlaybackPreparationException exception)
        {
            return Problem(
                statusCode: exception.StatusCode,
                title: exception.Code,
                detail: exception.Message);
        }
    }
}
