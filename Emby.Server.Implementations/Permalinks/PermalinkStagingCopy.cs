// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Copies caller-owned bytes into operation staging so the final staging path only ever
/// names a complete file. The bytes are written to a uniquely named temporary file in the
/// same directory, flushed to stable storage, and renamed atomically onto the final path.
/// An interrupted copy therefore leaves at most a temporary file that is never mistaken for
/// the staged result and is removed by the next restage of the same path.
/// </summary>
internal static class PermalinkStagingCopy
{
    private const int TemporarySuffixLength = 32;

    /// <summary>Separates the final file name from the unique temporary suffix.</summary>
    public const string TemporaryMarker = ".sloptank-partial-";

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> create-exclusively and atomically.
    /// </summary>
    /// <param name="fileSystem">The durable file system used to flush the completed bytes.</param>
    /// <param name="source">The existing source file.</param>
    /// <param name="destination">The final staging path; it must not exist.</param>
    /// <param name="beforePublish">Optional work applied to the complete temporary file before it is published.</param>
    /// <param name="cancellationToken">Cancels the copy; the temporary file is removed and nothing is published.</param>
    /// <returns>A task representing the asynchronous copy.</returns>
    /// <remarks>Throws <see cref="IOException"/> when <paramref name="destination"/> already exists.</remarks>
    public static async Task CopyAtomicAsync(
        IPermalinkAtomicFileSystem fileSystem,
        string source,
        string destination,
        Action<string>? beforePublish,
        CancellationToken cancellationToken)
    {
        RemoveStaleTemporaries(destination);
        var temporary = destination + TemporaryMarker + Guid.NewGuid().ToString("N");
        var published = false;
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            fileSystem.SyncFile(temporary);
            beforePublish?.Invoke(temporary);

            // File.Move without overwrite is an atomic rename on one volume and fails
            // if another writer published the destination first.
            File.Move(temporary, destination);
            published = true;
            fileSystem.SyncFile(destination);
        }
        finally
        {
            if (!published && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Deletes temporary copies left beside <paramref name="destination"/> by an interrupted copy of that same path.
    /// </summary>
    /// <param name="destination">The final staging path whose leftovers are removed.</param>
    /// <remarks>Only files named <c>&lt;destination file name&gt;.sloptank-partial-&lt;guid&gt;</c> in the destination's own directory are touched.</remarks>
    public static void RemoveStaleTemporaries(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var prefix = Path.GetFileName(destination) + TemporaryMarker;
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(candidate);

            // Exact shape only (prefix plus one 32-digit Guid), so a real tree member
            // that merely resembles a temporary name is never deleted.
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && name.Length == prefix.Length + TemporarySuffixLength
                && Guid.TryParseExact(name[prefix.Length..], "N", out _))
            {
                File.Delete(candidate);
            }
        }
    }
}
