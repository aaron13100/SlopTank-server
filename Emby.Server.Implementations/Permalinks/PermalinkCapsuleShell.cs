// SlopTank modification notice: added or changed by SlopTank on 2026-09-30.
using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Recognizes and removes a capsule-shell phantom: a directory sitting where a
/// file-backed Movie or Episode's media file used to be, holding nothing but the
/// item's own capsule directory.
/// </summary>
internal static class PermalinkCapsuleShell
{
    // Item kinds whose recorded path is a media FILE. Mirrors the shape rule in
    // tools/delete_unreachable_items.py: every other kind legitimately records a directory.
    private static readonly string[] _fileBackedKinds = ["Episode", "Movie"];

    /// <summary>Whether the directory at the path is exactly a capsule shell.</summary>
    /// <param name="path">The recorded content path.</param>
    /// <param name="itemKind">The item kind whose capsule name the shell must carry.</param>
    /// <returns>True for a shell; false for anything else.</returns>
    public static bool IsCapsuleShellDirectory(string? path, string itemKind)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var capsule = Path.Combine(path, CapsuleDirectoryName(itemKind));
            var entries = Directory.GetFileSystemEntries(path);
            return entries.Length == 1
                && string.Equals(entries[0], capsule, StringComparison.Ordinal)
                && Directory.Exists(capsule)
                && (File.GetAttributes(capsule) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A destructive predicate fails closed: an unreadable directory is not "a shell".
            // Nothing is cached, so the shape is re-evaluated on the next call.
            _ = exception;
            return false;
        }
    }

    /// <summary>Whether the item's recorded path is a capsule-shell phantom.</summary>
    /// <param name="item">The item.</param>
    /// <returns>True for a file-backed item whose path is a shell.</returns>
    public static bool IsPhantomShell(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is not (Movie or Episode)
            || item is Video { VideoType: VideoType.Dvd or VideoType.BluRay })
        {
            return false;
        }

        return IsCapsuleShellDirectory(item.Path, item.GetType().Name);
    }

    /// <summary>Whether the item's content is gone: absent, or a phantom shell.</summary>
    /// <param name="item">The item.</param>
    /// <returns>True when no playable content exists at the recorded path.</returns>
    public static bool IsVanishedContent(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        return (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            || IsPhantomShell(item);
    }

    /// <summary>Removes the shell at the path when, and only when, it is exactly a shell.</summary>
    /// <remarks>
    /// The authority's recorded capsule_path is the root-level capsule, left untouched
    /// exactly as a vanished-file deletion leaves it. The shape is re-checked right
    /// before deletion and the shell directory is removed non-recursively, so a file
    /// that appeared meanwhile makes the removal fail instead of being lost.
    /// </remarks>
    /// <param name="path">The recorded content path.</param>
    /// <returns>True when a shell was removed.</returns>
    public static bool TryRemove(string? path)
    {
        foreach (var kind in _fileBackedKinds)
        {
            if (!IsCapsuleShellDirectory(path, kind))
            {
                continue;
            }

            Directory.Delete(Path.Combine(path!, CapsuleDirectoryName(kind)), recursive: true);
            Directory.Delete(path!, recursive: false);
            return true;
        }

        return false;
    }

    private static string CapsuleDirectoryName(string itemKind)
    {
        return "." + itemKind.ToLowerInvariant() + ".sloptank-permalink";
    }
}
