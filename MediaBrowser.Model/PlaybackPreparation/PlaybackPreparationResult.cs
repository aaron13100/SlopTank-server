// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// The planner's classification and, when needed, its bounded preparation plan.
/// </summary>
public sealed class PlaybackPreparationResult
{
    /// <summary>Gets or sets the contract discriminator.</summary>
    [JsonPropertyName("contractType")]
    public string? ContractType { get; set; }

    /// <summary>Gets or sets the wire-contract version.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; set; }

    /// <summary>Gets or sets the planner algorithm version.</summary>
    [JsonPropertyName("plannerAlgorithmVersion")]
    public string? PlannerAlgorithmVersion { get; set; }

    /// <summary>Gets or sets the echoed request correlation identifier.</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>Gets or sets the echoed random request nonce.</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>Gets or sets the echoed library item identifier.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the echoed exact absolute path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Gets or sets the echoed media signature.</summary>
    [JsonPropertyName("mediaSignature")]
    public string? MediaSignature { get; set; }

    /// <summary>Gets or sets the planner outcome.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>Gets or sets the machine-readable reason code.</summary>
    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    /// <summary>Gets or sets the optional preparation plan.</summary>
    [JsonPropertyName("plan")]
    public PlaybackPreparationPlan? Plan { get; set; }
}
