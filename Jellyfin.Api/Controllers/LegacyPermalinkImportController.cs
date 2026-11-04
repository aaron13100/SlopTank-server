// SlopTank modification notice: added or changed by SlopTank on 2026-11-05.
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Permalinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Runs the legacy permalink alias import for a current library.</summary>
[Route("Permalinks")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class LegacyPermalinkImportController : BaseJellyfinApiController
{
    private readonly ILegacyPermalinkImporter _importer;

    /// <summary>Initializes a new instance of the <see cref="LegacyPermalinkImportController"/> class.</summary>
    /// <param name="importer">The existing legacy permalink importer.</param>
    public LegacyPermalinkImportController(ILegacyPermalinkImporter importer)
    {
        _importer = importer;
    }

    /// <summary>Retries importing previously issued legacy permalink aliases.</summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>No content after the import completes.</returns>
    [HttpPost("LegacyImport")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Import(CancellationToken cancellationToken)
    {
        await _importer.RunAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
