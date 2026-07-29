using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Admits only the reviewed local macOS filesystems for permalink mutation.
/// </summary>
public sealed class MacPermalinkMountPolicy
{
    private const uint LocalMount = 0x00001000;
    private const uint JournaledMount = 0x00800000;
    private readonly bool _allowTestMounts;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacPermalinkMountPolicy"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    public MacPermalinkMountPolicy(IConfiguration configuration)
    {
        _allowTestMounts = configuration.GetValue("Permalinks:AllowTestMounts", false);
    }

    /// <summary>
    /// Rejects paths outside local APFS or journaled HFS+ volumes.
    /// </summary>
    /// <param name="path">The filesystem path.</param>
    public void EnsureAdmitted(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw NotAdmitted(path, "the host is not macOS");
        }

        if (_allowTestMounts)
        {
            return;
        }

        var existingPath = FindExistingAncestor(path);
        var buffer = Marshal.AllocHGlobal(2168);
        try
        {
            if (StatFs(existingPath, buffer) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                var cause = new Win32Exception(error);
                throw new PermalinkException(
                    PermalinkErrorKind.Unavailable,
                    "mount-inspection-failed",
                    $"Cannot inspect permalink mount for '{path}' ({error}: {cause.Message}).",
                    cause);
            }

            var flags = unchecked((uint)Marshal.ReadInt32(buffer, 64));
            var typeName = Marshal.PtrToStringAnsi(
                IntPtr.Add(buffer, 72),
                16)?.TrimEnd('\0') ?? string.Empty;
            var isLocal = (flags & LocalMount) != 0;
            var isApfs = string.Equals(typeName, "apfs", StringComparison.Ordinal);
            var isJournaledHfs = string.Equals(
                    typeName,
                    "hfs",
                    StringComparison.Ordinal)
                && (flags & JournaledMount) != 0;
            if (!isLocal || (!isApfs && !isJournaledHfs))
            {
                throw NotAdmitted(
                    path,
                    $"filesystem '{typeName}' has flags 0x{flags:x}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FindExistingAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw NotAdmitted(path, "no existing ancestor can be inspected");
        }

        return current;
    }

    private static PermalinkException NotAdmitted(string path, string reason)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "mount-not-admitted",
            $"Permalink mutation at '{path}' requires local APFS or journaled HFS+ ({reason}).");
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "statfs$INODE64", SetLastError = true)]
    private static extern int StatFs(
        string path,
        IntPtr statistics);
}
