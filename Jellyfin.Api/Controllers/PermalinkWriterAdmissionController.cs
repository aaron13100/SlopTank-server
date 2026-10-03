// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-04.
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
[Route("")]
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
    public Task<ActionResult<PermalinkWriterAdmission>> Issue(
        [FromBody] PermalinkWriterAdmissionRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.IssueAsync(request, cancellationToken));

    /// <summary>Atomically records a retirement destination before any hardlink or copy.</summary>
    /// <param name="request">The exact source, absent destination and destination-parent binding.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The issued writer admission.</returns>
    [HttpPost("Permalinks/Authority/RetirementAdmissions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<PermalinkWriterAdmission>> IssueRetirement(
        [FromBody] PermalinkRetirementAdmissionRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.IssueRetirementAsync(request, cancellationToken));

    /// <summary>Reads the current admission immediately before publication.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current admission state.</returns>
    [HttpGet("Permalinks/Authority/WriterAdmissions/{admissionId:guid}")]
    public Task<ActionResult<PermalinkWriterAdmission>> Poll(Guid admissionId, CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.PollAsync(admissionId, cancellationToken));

    /// <summary>Starts an issued admission.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The started admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Start")]
    public Task<ActionResult<PermalinkWriterAdmission>> Start(Guid admissionId, CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.StartAsync(admissionId, cancellationToken));

    /// <summary>Renews the active writer heartbeat.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renewed admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Heartbeat")]
    public Task<ActionResult<PermalinkWriterAdmission>> Heartbeat(Guid admissionId, CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.HeartbeatAsync(admissionId, cancellationToken));

    /// <summary>Completes an active writer admission.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal admission state.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/Complete")]
    public Task<ActionResult<PermalinkWriterAdmission>> Complete(Guid admissionId, CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.CompleteAsync(admissionId, cancellationToken));

    /// <summary>Re-attests and durably registers a published retirement copy.</summary>
    /// <param name="admissionId">The retirement operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed or refused admission.</returns>
    [HttpPost("Permalinks/Authority/WriterAdmissions/{admissionId:guid}/SettleRetirement")]
    public Task<ActionResult<PermalinkWriterAdmission>> SettleRetirement(Guid admissionId, CancellationToken cancellationToken)
        => ExecuteAsync(() => _admissions.SettleRetirementAsync(admissionId, cancellationToken));

    private async Task<ActionResult<PermalinkWriterAdmission>> ExecuteAsync(
        Func<Task<PermalinkWriterAdmission>> action)
    {
        try
        {
            return Ok(await action().ConfigureAwait(false));
        }
        catch (PermalinkException exception)
        {
            return Problem(
                detail: exception.Message,
                statusCode: exception.Kind is PermalinkErrorKind.Unavailable
                    or PermalinkErrorKind.Corrupt
                    or PermalinkErrorKind.VersionRefused
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status409Conflict,
                title: exception.Code);
        }
    }
}
