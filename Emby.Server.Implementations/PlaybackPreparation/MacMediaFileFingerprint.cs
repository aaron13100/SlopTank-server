// SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
using System;
using System.Runtime.InteropServices;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Reads a macOS file generation token for playback preparation safety.</summary>
/// <remarks>
/// The macOS token detects same-size, same-timestamp replacements while a worker is active.
/// Other platforms retain the existing FileInfo fallback in <see cref="MediaFileFingerprint"/>.
/// </remarks>
internal static class MacMediaFileFingerprint
{
    private const int StatBufferSize = 144;
    private const int DeviceOffset = 0;
    private const int InodeOffset = 8;
    private const int ChangeTimeSecondsOffset = 64;
    private const int ChangeTimeNanosecondsOffset = 72;
    private const int SizeOffset = 96;

    /// <summary>Reads the macOS file generation token when available.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The macOS generation token, or null off macOS or when stat fails.</returns>
    public static MacMediaFileFingerprintToken? TryRead(string path)
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

            return new MacMediaFileFingerprintToken(
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

    /// <summary>A macOS device, inode, change-time and length snapshot.</summary>
    /// <param name="Device">The containing device identifier.</param>
    /// <param name="Inode">The filesystem inode.</param>
    /// <param name="ChangeTimeSeconds">The whole-second change time.</param>
    /// <param name="ChangeTimeNanoseconds">The sub-second change time.</param>
    /// <param name="Length">The file length at the same stat call.</param>
    internal readonly record struct MacMediaFileFingerprintToken(
        int Device,
        ulong Inode,
        long ChangeTimeSeconds,
        long ChangeTimeNanoseconds,
        long Length);
}
