// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-02.
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// macOS implementation of <see cref="IPermalinkPathObjects"/>: libc <c>realpath</c> and
/// <c>lstat$INODE64</c>, so the final path component is never symlink-resolved.
/// </summary>
public sealed class MacPermalinkPathObjects : IPermalinkPathObjects
{
    // Darwin 64-bit-inode struct stat: sizeof is 144, st_dev (int32) at 0, st_ino (uint64) at 8.
    // Same layout MacPermalinkContentIdentity reads for stat$INODE64.
    private const int StatBufferSize = 144;
    private const int DeviceOffset = 0;
    private const int InodeOffset = 8;
    private const int ModeOffset = 4;
    private const int FileTypeMask = 0xF000;
    private const int SymbolicLinkType = 0xA000;
    private const int PathMax = 1024;

    /// <inheritdoc />
    public string? CanonicalizeDirectory(string path)
    {
        var buffer = Marshal.AllocHGlobal(PathMax);
        try
        {
            if (RealPath(path, buffer) == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public string? ReadPinnedIdentityNoFollow(string path) => ReadPinnedObjectNoFollow(path)?.Identity;

    /// <inheritdoc />
    public PermalinkPinnedObject? ReadPinnedObjectNoFollow(string path)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            if (LStat(path, buffer) != 0)
            {
                return null;
            }

            var device = Marshal.ReadInt32(buffer, DeviceOffset);
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, InodeOffset));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, ModeOffset));
            return new PermalinkPinnedObject(
                string.Create(CultureInfo.InvariantCulture, $"{device}:{inode}"),
                (mode & FileTypeMask) == SymbolicLinkType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public byte[]? ReadFileIfExists(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStat(string path, IntPtr buffer);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(string path, IntPtr resolved);
}
