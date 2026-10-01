// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.IO;
using System.Text;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Builds the normalized relative-path half of a lease key (P6 decisions, "Lease key").
/// </summary>
internal static class PermalinkPathKey
{
    /// <summary>
    /// Normalizes a path below a content root to NFC with <c>/</c> separators.
    /// </summary>
    /// <param name="root">The content root the path lives under.</param>
    /// <param name="path">The content path. Its final component is never symlink-resolved.</param>
    /// <returns>The normalized relative path: no leading slash, no empty, <c>.</c> or <c>..</c> components.</returns>
    /// <exception cref="PermalinkException">Ineligible when the path is the root itself or escapes it.</exception>
    public static string Normalize(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        var components = relative.Split(Path.DirectorySeparatorChar);
        foreach (var component in components)
        {
            if (component.Length == 0 || component == "." || component == "..")
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Ineligible,
                    "path-key-invalid",
                    $"Path '{path}' has no valid key below content root '{root}'.");
            }
        }

        // HFS+ stores names as NFD while Jellyfin hands out whatever the scan saw, so one file
        // must not get two keys depending on which spelling reached the lease.
        return string.Join('/', components).Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Composes the single string a keyed lock is held on.
    /// </summary>
    /// <param name="rootIdentityId">The root identity.</param>
    /// <param name="normalizedRelativePath">The normalized relative path.</param>
    /// <returns>The lock key.</returns>
    public static string LockKey(string rootIdentityId, string normalizedRelativePath)
        => rootIdentityId + "\n" + normalizedRelativePath;
}
