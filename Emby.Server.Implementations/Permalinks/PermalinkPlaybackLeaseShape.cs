// SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Validates and non-recursively removes the bounded metadata-only playback-lease shape.
/// </summary>
internal static class PermalinkPlaybackLeaseShape
{
    private const int MaximumNodesPerLease = 256;
    private static readonly HashSet<string> _knownRootFiles = new(StringComparer.Ordinal)
    {
        "completed.json",
        "consumed.json",
        "lease.json",
        "plan.json",
        "ready.json"
    };

    public static bool TryDelete(string directory)
    {
        if (!TryCollect(directory, out var files, out var directories))
        {
            return false;
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }

        foreach (var childDirectory in directories)
        {
            Directory.Delete(childDirectory, recursive: false);
        }

        Directory.Delete(directory, recursive: false);
        return true;
    }

    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        try
        {
            // LinkTarget detects dangling links, for which Directory.Exists is false.
            if (info.LinkTarget is not null)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryCollect(
        string directory,
        out IReadOnlyList<string> files,
        out IReadOnlyList<string> directories)
    {
        var foundFiles = new List<string>();
        var ordinalDirectories = new List<string>();
        string? entriesDirectory = null;
        var examined = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++examined > MaximumNodesPerLease || IsLink(child))
            {
                return Failed(out files, out directories);
            }

            var name = Path.GetFileName(child);
            if (File.Exists(child))
            {
                if (!_knownRootFiles.Contains(name))
                {
                    return Failed(out files, out directories);
                }

                foundFiles.Add(child);
            }
            else if (Directory.Exists(child)
                     && string.Equals(name, "entries", StringComparison.Ordinal)
                     && entriesDirectory is null)
            {
                entriesDirectory = child;
            }
            else
            {
                return Failed(out files, out directories);
            }
        }

        if (entriesDirectory is not null)
        {
            foreach (var ordinalDirectory in Directory.EnumerateFileSystemEntries(entriesDirectory))
            {
                if (++examined > MaximumNodesPerLease
                    || IsLink(ordinalDirectory)
                    || !Directory.Exists(ordinalDirectory)
                    || !IsCanonicalOrdinal(Path.GetFileName(ordinalDirectory)))
                {
                    return Failed(out files, out directories);
                }

                foreach (var ordinalChild in Directory.EnumerateFileSystemEntries(ordinalDirectory))
                {
                    if (++examined > MaximumNodesPerLease
                        || IsLink(ordinalChild)
                        || !File.Exists(ordinalChild)
                        || !string.Equals(
                            Path.GetFileName(ordinalChild),
                            "ready.json",
                            StringComparison.Ordinal))
                    {
                        return Failed(out files, out directories);
                    }

                    foundFiles.Add(ordinalChild);
                }

                ordinalDirectories.Add(ordinalDirectory);
            }

            ordinalDirectories.Add(entriesDirectory);
        }

        files = foundFiles;
        directories = ordinalDirectories;
        return true;
    }

    private static bool IsCanonicalOrdinal(string name)
    {
        return int.TryParse(
                name,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal)
            && ordinal >= 0
            && string.Equals(
                ordinal.ToString(CultureInfo.InvariantCulture),
                name,
                StringComparison.Ordinal);
    }

    private static bool Failed(
        out IReadOnlyList<string> files,
        out IReadOnlyList<string> directories)
    {
        files = Array.Empty<string>();
        directories = Array.Empty<string>();
        return false;
    }
}
