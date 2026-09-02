using System;
using System.IO;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Reads the constant-time filesystem identity that detects a replaced media
/// object.
/// </summary>
/// <remarks>
/// Playback verification is on the request path for every shared link, so it
/// must stay O(1) in the size of the media. A replacement always produces a new
/// filesystem object, so creation time, length and last-write time together
/// identify the exact generation a lease was issued against without reading a
/// single content byte. This is the single source of truth for that fingerprint:
/// lease evidence and frozen playback plans must not drift apart on how a
/// replaced object is recognised.
/// </remarks>
internal static class PermalinkObjectIdentity
{
    /// <summary>
    /// Reads the identity fingerprint for a media file or directory.
    /// </summary>
    /// <param name="path">The absolute path to fingerprint.</param>
    /// <returns>
    /// The fingerprint, or <see langword="null"/> when nothing exists at the
    /// path. Callers decide which typed conflict a missing object represents.
    /// </returns>
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
