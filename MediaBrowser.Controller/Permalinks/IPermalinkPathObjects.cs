// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
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
    /// Reads a small regular file, or reports that it is absent.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The file bytes, or <see langword="null"/> when the file does not exist.</returns>
    byte[]? ReadFileIfExists(string path);
}
