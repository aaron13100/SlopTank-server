// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09.
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
/// filesystem object, so device, inode, status change time and length together
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
        // Prefer the filesystem change token: device and inode identify which
        // object this is, and status change time plus length prove it has not
        // been altered since capture. Creation time, length and modification
        // time are NOT a sound substitute: permalink-url-design.md records that
        // both survive a content-preserving same-second replacement, because
        // modification time can be reset by the very call that replaced the
        // bytes. That is also why PermalinkEvidence keys its digest cache on
        // this token, and the two must agree on what "the same object" means.
        if (MacPermalinkContentIdentity.TryRead(path) is { } token)
        {
            return string.Join(
                ":",
                "object",
                token.Device,
                token.Inode,
                token.ChangeTimeSeconds,
                token.ChangeTimeNanoseconds,
                token.Length);
        }

        // The token is macOS-only. Off that platform, fall back to the weaker
        // stat fingerprint rather than failing closed, which preserves the
        // pre-existing lease-evidence behaviour on hosts the server does not
        // ship to.
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
