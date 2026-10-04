// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// The structured work plan returned when a media item needs preparation.
/// </summary>
public sealed class PlaybackPreparationPlan
{
    /// <summary>Gets or sets the target filename suffix.</summary>
    [JsonPropertyName("targetSuffix")]
    public string? TargetSuffix { get; set; }

    /// <summary>Gets or sets whether video preparation is required.</summary>
    [JsonPropertyName("needsVideo")]
    public bool? NeedsVideo { get; set; }

    /// <summary>Gets or sets whether audio preparation is required.</summary>
    [JsonPropertyName("needsAudio")]
    public bool? NeedsAudio { get; set; }

    /// <summary>Gets or sets whether container preparation is required.</summary>
    [JsonPropertyName("needsContainer")]
    public bool? NeedsContainer { get; set; }

    /// <summary>Gets or sets whether faststart preparation is required.</summary>
    [JsonPropertyName("needsFaststart")]
    public bool? NeedsFaststart { get; set; }

    /// <summary>Gets or sets the additive audio tracks to create.</summary>
    [JsonPropertyName("addedAudio")]
    public PlaybackPreparationAudioTrack[]? AddedAudio { get; set; }

    /// <summary>Gets or sets the source indexes whose loudness was measured.</summary>
    [JsonPropertyName("loudnessMeasuredSourceIndexes")]
    public int[]? LoudnessMeasuredSourceIndexes { get; set; }
}
