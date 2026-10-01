// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Permalinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Authenticated external-writer admission endpoints.</summary>
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class PermalinkWriterAdmissionController : BaseJellyfinApiController
{
    private readonly IPermalinkWriterAdmissionService _admissions;

    /// <summary>Initializes a new instance of the <see cref="PermalinkWriterAdmissionController"/> class.</summary>
    /// <param name="admissions">The durable admission service.</param>
    public PermalinkWriterAdmissionController(IPermalinkWriterAdmissionService admissions)
    {
        _admissions = admissions;
    }

    /// <summary>Issues a five-second single-use writer admission.</summary>
    /// <param name="request">The canonical path and pinned-object request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The issued admission.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PermalinkWriterAdmission>> Issue(
        [FromBody] PermalinkWriterAdmissionRequest request,
        CancellationToken cancellationToken)
        => Ok(await _admissions.IssueAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>Reads the current admission immediately before publication.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current admission state.</returns>
    [HttpGet("Permalinks/Authority/WriterAdmissions/{admissionId:guid}")]
    public async Task<ActionResult<PermalinkWriterAdmission>> Poll(Guid admissionId, CancellationToken cancellationToken)
        => Ok(await _admissions.PollAsync(admissionId, cancellationToken).ConfigureAwait(false));

    /// <summary>Starts an issued admission.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The started admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Start")]
    public async Task<ActionResult<PermalinkWriterAdmission>> Start(Guid admissionId, CancellationToken cancellationToken)
        => Ok(await _admissions.StartAsync(admissionId, cancellationToken).ConfigureAwait(false));

    /// <summary>Renews the active writer heartbeat.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renewed admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Heartbeat")]
    public async Task<ActionResult<PermalinkWriterAdmission>> Heartbeat(Guid admissionId, CancellationToken cancellationToken)
        => Ok(await _admissions.HeartbeatAsync(admissionId, cancellationToken).ConfigureAwait(false));

    /// <summary>Completes an active writer admission.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Complete")]
    public async Task<ActionResult<PermalinkWriterAdmission>> Complete(Guid admissionId, CancellationToken cancellationToken)
        => Ok(await _admissions.CompleteAsync(admissionId, cancellationToken).ConfigureAwait(false));
}
