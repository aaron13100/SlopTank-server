// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// A request asking the compatibility planner to classify one exact media file.
/// </summary>
public sealed class PlaybackPreparationRequest
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

    /// <summary>Gets or sets the request correlation identifier.</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>Gets or sets the random request nonce.</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>Gets or sets the library item identifier.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the exact absolute path being classified.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Gets or sets the signature of the media at <see cref="Path"/>.</summary>
    [JsonPropertyName("mediaSignature")]
    public string? MediaSignature { get; set; }
}
