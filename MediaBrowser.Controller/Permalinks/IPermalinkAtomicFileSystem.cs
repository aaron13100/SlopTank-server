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
    Task<string> GetOrCreateAnchorTokenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an existing persistent anchor token without creating one.
    /// </summary>
    string? ReadAnchorToken(string path);

    /// <summary>
    /// Durably creates each missing path component.
    /// </summary>
    void CreateDirectoryDurable(string path);

    /// <summary>
    /// Publishes immutable canonical bytes create-exclusively.
    /// </summary>
    Task PublishImmutableAsync(
        string destination,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a fully synced directory create-exclusively.
    /// </summary>
    void PublishDirectoryImmutable(string temporaryDirectory, string destination);
}
