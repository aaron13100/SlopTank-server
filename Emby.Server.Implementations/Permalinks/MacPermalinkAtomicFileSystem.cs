using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Implements stable anchors and immutable publication on admitted local macOS filesystems.
/// </summary>
public sealed class MacPermalinkAtomicFileSystem : IPermalinkAtomicFileSystem
{
    private const string AnchorName = "com.sloptank.permalink-anchor";
    private const int AlreadyExists = 17;
    private const int AttributeMissing = 93;
    private const int FullSync = 51;
    private const int OpenReadOnly = 0;
    private const int RenameExclusive = 0x00000004;
    private const int XattrCreate = 0x0002;
    private readonly MacPermalinkMountPolicy _mountPolicy;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacPermalinkAtomicFileSystem"/> class.
    /// </summary>
    public MacPermalinkAtomicFileSystem(MacPermalinkMountPolicy mountPolicy)
    {
        _mountPolicy = mountPolicy;
    }

    /// <inheritdoc />
    public Task<string> GetOrCreateAnchorTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _mountPolicy.EnsureAdmitted(path);
        var existing = ReadAnchorToken(path);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        Span<byte> token = stackalloc byte[16];
        RandomNumberGenerator.Fill(token);
        var bytes = token.ToArray();
        if (SetXattr(path, AnchorName, bytes, (nuint)bytes.Length, 0, XattrCreate) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == AlreadyExists)
            {
                return Task.FromResult(
                    ReadAnchorToken(path)
                    ?? throw FileSystemFailure("anchor-race", path, error));
            }

            throw FileSystemFailure("anchor-create", path, error);
        }

        if (Directory.Exists(path))
        {
            SyncDirectory(path);
        }
        else
        {
            SyncPath(path);
        }

        SyncDirectory(Path.GetDirectoryName(path)!);
        return Task.FromResult(Convert.ToHexStringLower(bytes));
    }

    /// <inheritdoc />
    public string? ReadAnchorToken(string path)
    {
        _mountPolicy.EnsureAdmitted(path);
        var bytes = new byte[16];
        var length = GetXattr(path, AnchorName, bytes, (nuint)bytes.Length, 0, 0);
        if (length < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == AttributeMissing)
            {
                return null;
            }

            throw FileSystemFailure("anchor-read", path, error);
        }

        if (length != bytes.Length)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "anchor-malformed",
                $"Stable anchor at '{path}' has invalid length {length}.");
        }

        return Convert.ToHexStringLower(bytes);
    }

    /// <inheritdoc />
    public void AssignAnchorToken(string path, string anchorToken)
    {
        _mountPolicy.EnsureAdmitted(path);
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(anchorToken);
        }
        catch (FormatException exception)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "anchor-malformed",
                $"Prepared anchor token '{anchorToken}' is malformed.",
                exception);
        }

        if (bytes.Length != 16
            || SetXattr(path, AnchorName, bytes, (nuint)bytes.Length, 0, XattrCreate) != 0)
        {
            throw FileSystemFailure("anchor-assign", path, Marshal.GetLastPInvokeError());
        }

        SyncPath(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    /// <inheritdoc />
    public void CreateDirectoryDurable(string path)
    {
        _mountPolicy.EnsureAdmitted(path);
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return;
        }

        var missing = new Stack<string>();
        var cursor = fullPath;
        while (!Directory.Exists(cursor))
        {
            missing.Push(cursor);
            cursor = Path.GetDirectoryName(cursor)
                ?? throw new PermalinkException(
                    PermalinkErrorKind.Unavailable,
                    "directory-root",
                    $"Cannot find an existing parent for '{fullPath}'.");
        }

        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            SyncDirectory(directory);
            SyncDirectory(Path.GetDirectoryName(directory)!);
        }
    }

    /// <inheritdoc />
    public async Task PublishImmutableAsync(
        string destination,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        _mountPolicy.EnsureAdmitted(destination);
        CreateDirectoryDurable(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            ".caller-temp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                FullSyncDescriptor(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), temporary);
            }

            RenameExclusivePath(temporary, destination);
            SyncDirectory(Path.GetDirectoryName(destination)!);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <inheritdoc />
    public void PublishDirectoryImmutable(string temporaryDirectory, string destination)
    {
        _mountPolicy.EnsureAdmitted(destination);
        SyncTree(temporaryDirectory);
        CreateDirectoryDurable(Path.GetDirectoryName(destination)!);
        RenameExclusivePath(temporaryDirectory, destination);
        SyncDirectory(Path.GetDirectoryName(destination)!);
    }

    private static void RenameExclusivePath(string source, string destination)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (RenameAtxNp(-2, source, -2, destination, RenameExclusive) != 0)
            {
                throw FileSystemFailure(
                    "publish-exclusive",
                    destination,
                    Marshal.GetLastPInvokeError());
            }

            return;
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Immutable destination already exists: {destination}");
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void SyncTree(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            SyncPath(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            SyncTree(child);
        }

        SyncDirectory(directory);
    }

    private static void SyncPath(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        FullSyncDescriptor(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), path);
    }

    private static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var descriptor = Open(path, OpenReadOnly);
        if (descriptor < 0)
        {
            throw FileSystemFailure("directory-open", path, Marshal.GetLastPInvokeError());
        }

        try
        {
            FullSyncDescriptor(descriptor, path);
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static void FullSyncDescriptor(int descriptor, string path)
    {
        if (OperatingSystem.IsMacOS() && Fcntl(descriptor, FullSync) != 0)
        {
            throw FileSystemFailure("fullfsync", path, Marshal.GetLastPInvokeError());
        }
    }

    private static PermalinkException FileSystemFailure(string operation, string path, int error)
    {
        var cause = new Win32Exception(error);
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            operation,
            $"Permalink filesystem operation '{operation}' failed for '{path}' ({error}: {cause.Message}).",
            cause);
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(
        string path,
        string name,
        byte[] value,
        nuint size,
        uint position,
        int options);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(
        string path,
        string name,
        byte[] value,
        nuint size,
        uint position,
        int options);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtxNp(
        int fromDirectory,
        string from,
        int toDirectory,
        string to,
        int flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command);
}
