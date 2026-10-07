// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-02, 2026-10-04, 2026-10-07.
using System;
using System.IO;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Read-only, no-follow inspection of filesystem objects for the permalink path-identity
/// design (P6): a canonical directory, the pinned device and inode of the final path
/// component, and small control files such as the content-root identity marker.
/// </summary>
public interface IPermalinkPathObjects
{
    /// <summary>
    /// Resolves every symlink in an existing directory path.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>The canonical absolute path, or <see langword="null"/> when it does not exist.</returns>
    string? CanonicalizeDirectory(string path);

    /// <summary>
    /// Reads the device and inode of the final path component without following a symlink.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The identity formatted as <c>device:inode</c>, or <see langword="null"/> when the path does not exist.</returns>
    string? ReadPinnedIdentityNoFollow(string path);

    /// <summary>
    /// Reads the pinned identity and the object kind of the final path component from one
    /// <c>lstat</c>, without following a symlink.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The pinned object, or <see langword="null"/> when the path does not exist.</returns>
    PermalinkPinnedObject? ReadPinnedObjectNoFollow(string path);

    /// <summary>
    /// Reads the final component without following it, returning null only for ENOENT or
    /// ENOTDIR. Other probe failures throw instead of being misclassified as absence.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The pinned object, or null only when the path is definitely absent.</returns>
    PermalinkPinnedObject? ReadPinnedObjectNoFollowExact(string path);

    /// <summary>Opens one regular file without following its final component and pins the opened descriptor.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="pinnedIdentity">The device and inode read from the opened descriptor.</param>
    /// <returns>The opened stream, or null only when the path is absent.</returns>
    FileStream? OpenReadNoFollowExact(string path, out string pinnedIdentity)
        => throw new NotSupportedException("This path inspector cannot open pinned no-follow files.");

    /// <summary>
    /// Reads a small regular file, or reports that it is absent.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The file bytes, or <see langword="null"/> when the file does not exist.</returns>
    byte[]? ReadFileIfExists(string path);
}
