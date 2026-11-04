// SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
using System;
using System.IO;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Reads the generation of a media file used by a pending preparation job.</summary>
/// <remarks>
/// This protects a physical file replacement that races with an encode or probe. It does not
/// identify logical media, and permalink creation or lookup must never depend on it.
/// </remarks>
internal static class MediaFileFingerprint
{
    /// <summary>Reads the file generation, or returns null when the path does not exist.</summary>
    /// <param name="path">The file or directory path.</param>
    /// <returns>A stable fingerprint for the observed generation, or null.</returns>
    public static string? Read(string path)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return string.Join(
                ":",
                "file",
                info.CreationTimeUtc.Ticks,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }

        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            return string.Join(
                ":",
                "directory",
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks);
        }

        return null;
    }
}
