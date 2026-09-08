using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles;

/// <summary>
/// Writes parsed subtitle events in the formats served by the subtitle API.
/// </summary>
public static class SubtitleTextWriter
{
    /// <summary>
    /// Serializes a parsed subtitle track in one supported wire format.
    /// </summary>
    /// <param name="track">The parsed events.</param>
    /// <param name="format">The requested format or alias.</param>
    /// <returns>The serialized subtitle text.</returns>
    public static string ToText(SubtitleTrackInfo track, string format)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        return format.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "ass" => WriteSubStationAlpha(track, advanced: true),
            "json" => WriteJson(track),
            "srt" or "subrip" => WriteSubRip(track),
            "ssa" => WriteSubStationAlpha(track, advanced: false),
            "ttml" => WriteTimedText(track),
            "vtt" or "webvtt" => WriteWebVtt(track),
            _ => throw new ArgumentException("Unsupported format: " + format, nameof(format))
        };
    }

    private static string WriteJson(SubtitleTrackInfo track)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("TrackEvents");
            foreach (var entry in track.TrackEvents)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", entry.Id);
                writer.WriteString("Text", entry.Text);
                writer.WriteNumber("StartPositionTicks", entry.StartPositionTicks);
                writer.WriteNumber("EndPositionTicks", entry.EndPositionTicks);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static string WriteSubRip(SubtitleTrackInfo track)
    {
        var result = new StringBuilder();
        foreach (var entry in track.TrackEvents)
        {
            result.AppendLine(entry.Id);
            result.Append(FormatTime(entry.StartPositionTicks, ',', 3))
                .Append(" --> ")
                .AppendLine(FormatTime(entry.EndPositionTicks, ',', 3));
            result.AppendLine(NormalizeNewlines(entry.Text));
            result.AppendLine();
        }

        return result.ToString();
    }

    private static string WriteWebVtt(SubtitleTrackInfo track)
    {
        var result = new StringBuilder("WEBVTT\n\n");
        foreach (var entry in track.TrackEvents)
        {
            result.Append(FormatTime(entry.StartPositionTicks, '.', 3))
                .Append(" --> ")
                .AppendLine(FormatTime(entry.EndPositionTicks, '.', 3));
            result.AppendLine(NormalizeNewlines(entry.Text));
            result.AppendLine();
        }

        return result.ToString();
    }

    private static string WriteSubStationAlpha(SubtitleTrackInfo track, bool advanced)
    {
        var result = new StringBuilder();
        result.AppendLine("[Script Info]")
            .AppendLine(advanced ? "ScriptType: v4.00+" : "ScriptType: v4.00")
            .AppendLine()
            .AppendLine("[Events]");
        if (advanced)
        {
            result.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        }
        else
        {
            result.AppendLine("Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        }

        foreach (var entry in track.TrackEvents)
        {
            result.Append(advanced ? "Dialogue: 0," : "Dialogue: Marked=0,")
                .Append(FormatAssTime(entry.StartPositionTicks))
                .Append(',')
                .Append(FormatAssTime(entry.EndPositionTicks))
                .Append(",Default,,0,0,0,,")
                .AppendLine(NormalizeNewlines(entry.Text).Replace("\n", "\\N", StringComparison.Ordinal));
        }

        return result.ToString();
    }

    private static string WriteTimedText(SubtitleTrackInfo track)
    {
        using var result = new MemoryStream();
        using (var writer = XmlWriter.Create(
                   result,
                   new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            const string Namespace = "http://www.w3.org/ns/ttml";
            writer.WriteStartElement("tt", Namespace);
            writer.WriteStartElement("body", Namespace);
            writer.WriteStartElement("div", Namespace);
            foreach (var entry in track.TrackEvents)
            {
                writer.WriteStartElement("p", Namespace);
                writer.WriteAttributeString("begin", FormatTime(entry.StartPositionTicks, '.', 3));
                writer.WriteAttributeString("end", FormatTime(entry.EndPositionTicks, '.', 3));
                var lines = NormalizeNewlines(entry.Text).Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index > 0)
                    {
                        writer.WriteStartElement("br", Namespace);
                        writer.WriteEndElement();
                    }

                    writer.WriteString(lines[index]);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string FormatTime(long ticks, char decimalSeparator, int fractionDigits)
    {
        var totalMilliseconds = Math.Max(0, ticks / TimeSpan.TicksPerMillisecond);
        var hours = totalMilliseconds / 3_600_000;
        var minutes = (totalMilliseconds / 60_000) % 60;
        var seconds = (totalMilliseconds / 1_000) % 60;
        var milliseconds = totalMilliseconds % 1_000;
        var fraction = fractionDigits == 2 ? milliseconds / 10 : milliseconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00}{decimalSeparator}{fraction.ToString(fractionDigits == 2 ? "00" : "000", CultureInfo.InvariantCulture)}");
    }

    private static string FormatAssTime(long ticks)
    {
        var totalMilliseconds = Math.Max(0, ticks / TimeSpan.TicksPerMillisecond);
        var hours = totalMilliseconds / 3_600_000;
        var minutes = (totalMilliseconds / 60_000) % 60;
        var seconds = (totalMilliseconds / 1_000) % 60;
        var centiseconds = (totalMilliseconds % 1_000) / 10;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{minutes:00}:{seconds:00}.{centiseconds:00}");
    }
}
