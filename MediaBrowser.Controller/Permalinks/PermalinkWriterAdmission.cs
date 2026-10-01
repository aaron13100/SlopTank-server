// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>The durable state of one filesystem writer admission.</summary>
public sealed record PermalinkWriterAdmission(
    Guid AdmissionId,
    string Status,
    DateTimeOffset StartDeadlineUtc,
    DateTimeOffset? HeartbeatDeadlineUtc,
    long AuthorityEpoch,
    long ContentEpoch,
    bool Allowed);
