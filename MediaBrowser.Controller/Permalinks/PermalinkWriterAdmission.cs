// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// The durable state of one filesystem writer admission. Wire names are camelCase because the
/// external Python client (tools/lib/permalink_writer_admission.py) decodes exactly those names,
/// while the surrounding Jellyfin API serializes PascalCase.
/// </summary>
/// <param name="AdmissionId">The admission identifier.</param>
/// <param name="Status">The lifecycle state: issued, started, completed or recovery-required.</param>
/// <param name="StartDeadlineUtc">The deadline by which an issued admission must start.</param>
/// <param name="HeartbeatDeadlineUtc">The deadline by which a started admission must heartbeat.</param>
/// <param name="AuthorityEpoch">The authority protocol epoch bound at issue.</param>
/// <param name="ContentEpoch">The content epoch bound at issue.</param>
/// <param name="Allowed">Whether a writer may mutate right now.</param>
public sealed record PermalinkWriterAdmission(
    [property: JsonPropertyName("admissionId")] Guid AdmissionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startDeadlineUtc")] DateTimeOffset StartDeadlineUtc,
    [property: JsonPropertyName("heartbeatDeadlineUtc")] DateTimeOffset? HeartbeatDeadlineUtc,
    [property: JsonPropertyName("authorityEpoch")] long AuthorityEpoch,
    [property: JsonPropertyName("contentEpoch")] long ContentEpoch,
    [property: JsonPropertyName("allowed")] bool Allowed);
