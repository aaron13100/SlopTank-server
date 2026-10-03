// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-04.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Issues and advances durable external-writer admissions.</summary>
public interface IPermalinkWriterAdmissionService
{
    /// <summary>Atomically records a retirement-copy registry intent and its writer admission.</summary>
    /// <param name="request">The exact source and absent evidence-destination binding.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The issued admission, whose id is the caller's operation id.</returns>
    Task<PermalinkWriterAdmission> IssueRetirementAsync(
        PermalinkRetirementAdmissionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Issues an admission bound to the current live authority state.</summary>
    /// <param name="request">The canonical path and pinned-object request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The issued admission.</returns>
    Task<PermalinkWriterAdmission> IssueAsync(PermalinkWriterAdmissionRequest request, CancellationToken cancellationToken);

    /// <summary>Starts an issued admission before its five-second deadline.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The started or refused admission.</returns>
    Task<PermalinkWriterAdmission> StartAsync(Guid admissionId, CancellationToken cancellationToken);

    /// <summary>Renews an already-started writer heartbeat.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renewed or refused admission.</returns>
    Task<PermalinkWriterAdmission> HeartbeatAsync(Guid admissionId, CancellationToken cancellationToken);

    /// <summary>Reads the fail-closed state immediately before a physical mutation.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current admission state.</returns>
    Task<PermalinkWriterAdmission> PollAsync(Guid admissionId, CancellationToken cancellationToken);

    /// <summary>Marks a writer admission complete after publication settles.</summary>
    /// <param name="admissionId">The admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed or refused admission.</returns>
    Task<PermalinkWriterAdmission> CompleteAsync(Guid admissionId, CancellationToken cancellationToken);

    /// <summary>Commits a published retirement copy to the registry and completes its admission.</summary>
    /// <param name="admissionId">The retirement operation/admission identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed or refused admission.</returns>
    Task<PermalinkWriterAdmission> SettleRetirementAsync(Guid admissionId, CancellationToken cancellationToken);
}
