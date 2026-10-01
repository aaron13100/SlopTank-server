// SlopTank modification notice: added or changed by SlopTank on 2026-09-19, 2026-10-01.
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Permalinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Authenticated permalink authority freshness oracle.</summary>
[Authorize(Policy = Policies.RequiresElevation)]
[Route("")]
public sealed class PermalinkAuthorityController : BaseJellyfinApiController
{
    private readonly IPermalinkAuthorityGenerationOracle _oracle;

    /// <summary>Initializes a new instance of the <see cref="PermalinkAuthorityController"/> class.</summary>
    /// <param name="oracle">The live authority oracle.</param>
    public PermalinkAuthorityController(IPermalinkAuthorityGenerationOracle oracle)
    {
        _oracle = oracle;
    }

    /// <summary>Returns live counters and the latest published snapshot generation.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current authority state.</returns>
    [HttpGet("Permalinks/Authority/Generation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PermalinkAuthorityGenerationInfo>> GetGeneration(
        CancellationToken cancellationToken)
    {
        return Ok(await _oracle.GetGenerationAsync(cancellationToken).ConfigureAwait(false));
    }
}
