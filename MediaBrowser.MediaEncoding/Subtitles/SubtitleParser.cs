using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles;

/// <summary>
/// Parses the subtitle formats that must be read in-process.
/// </summary>
/// <remarks>
/// Uncommon external text formats remain on the existing FFmpeg conversion
/// path and arrive here as SubRip. Embedded ASS/SSA remains in-process so its
/// positioning and styling survive JSON conversion.
/// </remarks>
public sealed class SubtitleParser : ISubtitleParser
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass",
        "srt",
        "ssa",
        "subrip"
    };

    /// <inheritdoc />
    public SubtitleTrackInfo Parse(Stream stream, string fileExtension)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);

        var extension = NormalizeExtension(fileExtension);
        if (!SupportsFileExtension(extension))
        {
            throw new ArgumentException($"Unsupported file extension: {fileExtension}", nameof(fileExtension));
        }

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        var events = extension is "ass" or "ssa"
            ? ParseSubStationAlpha(lines)
            : ParseSubRip(lines);
        if (events.Count == 0)
        {
            throw new ArgumentException("Unsupported or malformed format: " + fileExtension, nameof(fileExtension));
        }

        return new SubtitleTrackInfo { TrackEvents = events };
    }

    /// <inheritdoc />
    public bool SupportsFileExtension(string fileExtension)
        => !string.IsNullOrWhiteSpace(fileExtension)
           && SupportedExtensions.Contains(NormalizeExtension(fileExtension));

    private static string NormalizeExtension(string extension)
        => extension.Trim().TrimStart('.');

    private static List<SubtitleTrackEvent> ParseSubRip(IReadOnlyList<string> lines)
    {
        var events = new List<SubtitleTrackEvent>();
        var position = 0;
        while (position < lines.Count)
        {
            while (position < lines.Count && string.IsNullOrWhiteSpace(lines[position]))
            {
                position++;
            }

            if (position >= lines.Count)
            {
                break;
            }

            var candidateStart = position;
            string id;
            if (TryParseRange(lines[position], out var start, out var end))
            {
                id = (events.Count + 1).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                id = lines[position].Trim();
                position++;
                if (position >= lines.Count || !TryParseRange(lines[position], out start, out end))
                {
                    position = candidateStart + 1;
                    continue;
                }
            }

            position++;
            var text = new List<string>();
            while (position < lines.Count)
            {
                if (string.IsNullOrWhiteSpace(lines[position]))
                {
                    var next = position;
                    while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next]))
                    {
                        next++;
                    }

                    if (next >= lines.Count || IsCueStart(lines, next))
                    {
                        position = next;
                        break;
                    }
                }

                text.Add(lines[position]);
                position++;
            }

            while (text.Count > 0 && string.IsNullOrWhiteSpace(text[^1]))
            {
                text.RemoveAt(text.Count - 1);
            }

            if (text.Count == 0 || end < start)
            {
                continue;
            }

            events.Add(new SubtitleTrackEvent(id, string.Join(Environment.NewLine, text))
            {
                StartPositionTicks = start,
                EndPositionTicks = end
            });
        }

        return events;
    }

    private static bool IsCueStart(IReadOnlyList<string> lines, int position)
        => TryParseRange(lines[position], out _, out _)
           || (position + 1 < lines.Count && TryParseRange(lines[position + 1], out _, out _));

    private static bool TryParseRange(string line, out long start, out long end)
    {
        start = 0;
        end = 0;
        var separator = line.IndexOf("-->", StringComparison.Ordinal);
        return separator >= 0
               && TryParseTime(line.AsSpan(0, separator), out start)
               && TryParseTime(line.AsSpan(separator + 3), out end);
    }

    private static List<SubtitleTrackEvent> ParseSubStationAlpha(IReadOnlyList<string> lines)
    {
        var events = new List<SubtitleTrackEvent>();
        var inEvents = false;
        string[] fields = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length > 0 && line[0] == '[')
            {
                inEvents = string.Equals(line, "[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line[7..].Split(',').Select(value => value.Trim()).ToArray();
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var startIndex = Array.FindIndex(fields, value => string.Equals(value, "Start", StringComparison.OrdinalIgnoreCase));
            var endIndex = Array.FindIndex(fields, value => string.Equals(value, "End", StringComparison.OrdinalIgnoreCase));
            var textIndex = Array.FindIndex(fields, value => string.Equals(value, "Text", StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0 || endIndex < 0 || textIndex < 0)
            {
                continue;
            }

            var values = line[9..].TrimStart().Split(',', fields.Length);
            if (values.Length != fields.Length
                || !TryParseTime(values[startIndex], out var start)
                || !TryParseTime(values[endIndex], out var end)
                || end < start)
            {
                continue;
            }

            var text = values[textIndex]
                .Replace("\\N", Environment.NewLine, StringComparison.Ordinal)
                .Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
            events.Add(new SubtitleTrackEvent(
                (events.Count + 1).ToString(CultureInfo.InvariantCulture),
                text)
            {
                StartPositionTicks = start,
                EndPositionTicks = end
            });
        }

        return events;
    }

    private static bool TryParseTime(ReadOnlySpan<char> value, out long ticks)
    {
        ticks = 0;
        value = value.Trim();
        var suffix = value.IndexOfAny(' ', '\t');
        if (suffix >= 0)
        {
            value = value[..suffix];
        }

        var parts = value.ToString().Split([':', ',', '.']);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var fraction)
            || hours < 0
            || minutes is < 0 or > 59
            || seconds is < 0 or > 59
            || parts[3].Length is < 1 or > 3)
        {
            return false;
        }

        var milliseconds = parts[3].Length switch
        {
            1 => fraction * 100,
            2 => fraction * 10,
            _ => fraction
        };
        try
        {
            ticks = checked(
                (hours * TimeSpan.TicksPerHour)
                + (minutes * TimeSpan.TicksPerMinute)
                + (seconds * TimeSpan.TicksPerSecond)
                + (milliseconds * TimeSpan.TicksPerMillisecond));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
