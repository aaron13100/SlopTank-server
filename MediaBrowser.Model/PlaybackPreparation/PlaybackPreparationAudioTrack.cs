// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// One additive audio track in a playback-preparation plan.
/// </summary>
public sealed class PlaybackPreparationAudioTrack
{
    /// <summary>Gets or sets the source stream index.</summary>
    [JsonPropertyName("sourceIndex")]
    public int? SourceIndex { get; set; }

    /// <summary>Gets or sets the normalized language code.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Gets or sets the output channel count.</summary>
    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    /// <summary>Gets or sets the planner's output bitrate string.</summary>
    [JsonPropertyName("bitrate")]
    public string? Bitrate { get; set; }

    /// <summary>Gets or sets whether the track should carry the default flag.</summary>
    [JsonPropertyName("makeDefault")]
    public bool? MakeDefault { get; set; }

    /// <summary>Gets or sets whether the source is commentary.</summary>
    [JsonPropertyName("isCommentary")]
    public bool? IsCommentary { get; set; }

    /// <summary>Gets or sets whether loudness normalization should be applied.</summary>
    [JsonPropertyName("applyLoudnorm")]
    public bool? ApplyLoudnorm { get; set; }
}
