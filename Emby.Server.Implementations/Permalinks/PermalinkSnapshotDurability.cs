// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Flushes snapshot files and rename-parent directories durably.</summary>
internal static class PermalinkSnapshotDurability
{
    public static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException($"Cannot open snapshot directory for fsync: {path}");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException($"Cannot fsync snapshot directory: {path}");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
