using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Reads and create-exclusively publishes canonical playback-state documents.
/// </summary>
internal sealed class PermalinkPlaybackDocumentStore
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkPlaybackRecovery _recovery;

    public PermalinkPlaybackDocumentStore(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkPlaybackRecovery recovery)
    {
        _authority = authority;
        _fileSystem = fileSystem;
        _recovery = recovery;
    }

    public async Task PublishLeaseAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        await _recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        var resolutionRoot = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-resolution-leases",
            handle);
        var source = new[] { "playback-lease.json", "lease.json" }
            .Select(fileName => Path.Combine(resolutionRoot, fileName))
            .FirstOrDefault(File.Exists)
            ?? throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "playback-lease-missing",
                $"Playback resolution lease '{handle}' was not durably published.");
        var root = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-playback-leases",
            handle);
        _fileSystem.CreateDirectoryDurable(root);
        var document = CanonicalJson.Deserialize<PermalinkLeaseDocument>(
            await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false),
            source);
        await PublishAsync(
            Path.Combine(root, "lease.json"),
            document,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken)
    {
        var bytes = CanonicalJson.Serialize(document);
        if (!File.Exists(path))
        {
            try
            {
                await _fileSystem.PublishImmutableAsync(path, bytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
            {
                // A competing transition published first; exact comparison decides the winner.
            }
        }

        var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!existing.AsSpan().SequenceEqual(bytes.Span))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "playback-state-conflict",
                $"Playback state '{path}' has different immutable input.");
        }
    }

    public static async Task<T> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        return CanonicalJson.Deserialize<T>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
    }
}
