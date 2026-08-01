using System;
using System.Runtime.InteropServices;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Reads the filesystem-guaranteed change token a full-content hash cache is
/// allowed to key on (permalink-url-design.md: "size and mtime alone are
/// forbidden"; the token must be file identity, change time and byte length
/// together). Device and inode identify which file the digest was computed
/// for; status change time (ctime) records that a userspace call altered the
/// inode, and unlike modification time it cannot be reset by the caller that
/// just replaced the content, so it survives exactly the same-mtime swap that
/// defeats a cache keyed on modification time and length alone.
/// </summary>
internal static class MacPermalinkContentIdentity
{
    // Layout of Darwin's 64-bit-inode `struct stat` (sys/stat.h), read at
    // fixed byte offsets rather than through a marshaled struct, matching
    // this project's existing statfs$INODE64 probe in
    // MacPermalinkMountPolicy. dlsym-based P/Invoke binding does not apply
    // the compiler's __DARWIN_INODE64 symbol substitution, so the entry
    // point names the 64-bit-inode symbol explicitly.
    // Verified against clang's offsetof/sizeof on this host (x86_64 Darwin
    // 21.6.0): sizeof(struct stat) is 144, not 128, so the previous buffer
    // was 16 bytes short of what the kernel writes into it -- a heap
    // overflow on every call, which is what crashed the test process with
    // SIGABRT. st_size sits at offset 96 (after st_ctimespec at 64 and
    // st_birthtimespec at 80), not 80; offset 80 is st_birthtimespec, so the
    // previous SizeOffset silently returned a birth-time timestamp instead
    // of the byte length.
    private const int StatBufferSize = 144;
    private const int DeviceOffset = 0;
    private const int InodeOffset = 8;
    private const int ChangeTimeSecondsOffset = 64;
    private const int ChangeTimeNanosecondsOffset = 72;
    private const int SizeOffset = 96;

    /// <summary>
    /// Reads the current change token for a path, or <see langword="null"/>
    /// when the host is not macOS or the path cannot be inspected (missing,
    /// permission denied, etc). A caller that gets <see langword="null"/>
    /// must treat the content as uncacheable and hash it directly.
    /// </summary>
    /// <param name="path">The filesystem path.</param>
    /// <returns>The change token, or <see langword="null"/>.</returns>
    public static MacPermalinkContentIdentityToken? TryRead(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            if (Stat(path, buffer) != 0)
            {
                return null;
            }

            return new MacPermalinkContentIdentityToken(
                Marshal.ReadInt32(buffer, DeviceOffset),
                unchecked((ulong)Marshal.ReadInt64(buffer, InodeOffset)),
                Marshal.ReadInt64(buffer, ChangeTimeSecondsOffset),
                Marshal.ReadInt64(buffer, ChangeTimeNanosecondsOffset),
                Marshal.ReadInt64(buffer, SizeOffset));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int Stat(string path, IntPtr buffer);
}
