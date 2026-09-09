// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-08-29, 2026-09-09.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Provides the narrow stable-anchor and immutable-publication capabilities required by permalinks.
/// </summary>
public interface IPermalinkAtomicFileSystem
{
    /// <summary>
    /// Gets or durably creates the persistent random anchor token on a filesystem object.
    /// </summary>
    /// <param name="path">The filesystem object path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The persistent anchor token.</returns>
    Task<string> GetOrCreateAnchorTokenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an existing persistent anchor token without creating one.
    /// </summary>
    /// <param name="path">The filesystem object path.</param>
    /// <returns>The existing anchor token, or <see langword="null"/>.</returns>
    string? ReadAnchorToken(string path);

    /// <summary>
    /// Assigns an exact prepared anchor token to a fresh staging object.
    /// </summary>
    /// <param name="path">The fresh staging object path.</param>
    /// <param name="anchorToken">The exact prepared anchor token.</param>
    void AssignAnchorToken(string path, string anchorToken);

    /// <summary>
    /// Durably creates each missing path component.
    /// </summary>
    /// <param name="path">The directory path.</param>
    void CreateDirectoryDurable(string path);

    /// <summary>
    /// Probes whether a directory can accept and durably flush a new file.
    /// </summary>
    /// <param name="path">The existing directory path.</param>
    void ProbeDirectoryWriteAccess(string path);

    /// <summary>
    /// Removes interrupted immutable publications while preserving live publishers.
    /// </summary>
    /// <param name="directory">The publication directory.</param>
    void CleanupAbandonedPublications(string directory);

    /// <summary>
    /// Flushes a completed file and its parent directory to stable storage.
    /// </summary>
    /// <param name="path">The completed file path.</param>
    void SyncFile(string path);

    /// <summary>
    /// Publishes immutable canonical bytes create-exclusively.
    /// </summary>
    /// <param name="destination">The immutable destination path.</param>
    /// <param name="contents">The canonical bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing durable publication.</returns>
    Task PublishImmutableAsync(
        string destination,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a fully synced directory create-exclusively.
    /// </summary>
    /// <param name="temporaryDirectory">The fully synced temporary directory.</param>
    /// <param name="destination">The create-exclusive destination path.</param>
    void PublishDirectoryImmutable(string temporaryDirectory, string destination);
}
