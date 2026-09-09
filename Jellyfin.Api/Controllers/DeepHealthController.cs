// SlopTank modification notice: added or changed by SlopTank on 2026-09-07, 2026-09-09.
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Health;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Exposes the bounded administrator-only subsystem health snapshot.
/// </summary>
[Route("System")]
public sealed class DeepHealthController : BaseJellyfinApiController
{
    private readonly IDeepHealthService _deepHealth;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepHealthController"/> class.
    /// </summary>
    /// <param name="deepHealth">Shared deep-health source.</param>
    public DeepHealthController(IDeepHealthService deepHealth)
    {
        _deepHealth = deepHealth;
    }

    /// <summary>
    /// Gets a named per-check health breakdown without starting work.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <response code="200">The current per-check snapshot.</response>
    /// <response code="401">Request is not authenticated.</response>
    /// <response code="403">User is not an administrator.</response>
    /// <returns>The bounded health report.</returns>
    [HttpGet("Health/Deep")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeepHealthReport>> GetDeepHealth(CancellationToken cancellationToken)
        => Ok(await _deepHealth.CheckAsync(cancellationToken).ConfigureAwait(false));
}
