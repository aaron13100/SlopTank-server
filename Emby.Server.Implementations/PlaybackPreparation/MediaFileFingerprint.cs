// SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-11.
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

    /// <summary>
    /// Reports whether the file at <paramref name="path"/> differs from the size and
    /// modification time a library row recorded for it.
    /// </summary>
    /// <remarks>
    /// Serves rows attested Ready before object identities existed, which carry only the
    /// facts the library saved alongside them. An unrecorded fact (no size, no time) or a
    /// missing file is not evidence of change; a later save compares again.
    /// </remarks>
    /// <param name="path">The file path.</param>
    /// <param name="recordedSize">The size the row recorded, if any.</param>
    /// <param name="recordedLastWriteUtc">The modification time the row recorded, if any.</param>
    /// <returns><c>true</c> when a recorded fact contradicts the current file.</returns>
    public static bool DiffersFromRecorded(string path, long? recordedSize, DateTime? recordedLastWriteUtc)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (recordedSize is > 0 && recordedSize.Value != info.Length)
        {
            return true;
        }

        return recordedLastWriteUtc is { } recorded
            && recorded != DateTime.MinValue
            && DateTime.SpecifyKind(recorded, DateTimeKind.Utc).Ticks != info.LastWriteTimeUtc.Ticks;
    }
}
