// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Acquires path leases: resolves the root identity, takes the keyed lock, pins the object and,
/// when the anchor is missing, writes it only after an anchor-intent row has committed.
/// </summary>
internal sealed class PermalinkPathLeaseFactory
{
    private readonly PermalinkRootIdentityRegistry _roots;
    private readonly PermalinkPathLocks _locks;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly IPermalinkPathObjects _objects;
    private readonly PermalinkPathEvidenceStore _evidence;
    private readonly PermalinkAuthorityStore _authority;

    public PermalinkPathLeaseFactory(
        PermalinkRootIdentityRegistry roots,
        PermalinkPathLocks locks,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects,
        PermalinkPathEvidenceStore evidence,
        PermalinkAuthorityStore authority)
    {
        _roots = roots;
        _locks = locks;
        _fileSystem = fileSystem;
        _objects = objects;
        _evidence = evidence;
        _authority = authority;
    }

    /// <summary>
    /// Begins a lease on one content path.
    /// </summary>
    /// <param name="configuredRoot">The configured content root the path lives under.</param>
    /// <param name="path">The content path (its final component is never symlink-resolved).</param>
    /// <param name="isVirtual">Whether the path is a virtual season directory with no stable inode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease; dispose it after Ensure settles or fails.</returns>
    /// <exception cref="PermalinkException">Unavailable <c>permalink-root-identity-unknown</c> for an UNKNOWN root.</exception>
    public async Task<PermalinkPathLease> BeginAsync(
        string configuredRoot,
        string path,
        bool isVirtual,
        CancellationToken cancellationToken)
    {
        var root = await _roots.EnsureKnownAsync(configuredRoot, cancellationToken).ConfigureAwait(false);
        var relative = PermalinkPathKey.Normalize(configuredRoot, path);
        var keyLock = await _locks.AcquireAsync(
            PermalinkPathKey.LockKey(root.RootIdentityId, relative),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var pin = isVirtual
                ? PermalinkPathLease.VirtualPin
                : _objects.ReadPinnedIdentityNoFollow(path)
                    ?? throw new PermalinkException(
                        PermalinkErrorKind.Unavailable,
                        "content-unreachable",
                        $"Permalink content is unreachable at '{path}'.");
            var epoch = await _evidence.ReadContentEpochAsync(cancellationToken).ConfigureAwait(false);
            long generation;
            await using (var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                generation = await _evidence.ReadMaxReservationGenerationAsync(
                    connection, null, root.RootIdentityId, relative, cancellationToken).ConfigureAwait(false);
            }

            var anchor = _fileSystem.ReadAnchorToken(path);
            Guid? intent = null;
            if (anchor is null)
            {
                (anchor, intent) = await CreateAnchorUnderIntentAsync(root, relative, path, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new PermalinkPathLease(
                keyLock,
                path,
                root,
                relative,
                pin,
                anchor,
                intent,
                generation,
                epoch,
                _fileSystem,
                _objects,
                _evidence);
        }
        catch
        {
            keyLock.Dispose();
            throw;
        }
    }

    private async Task<(string Token, Guid? Intent)> CreateAnchorUnderIntentAsync(
        PermalinkRootIdentity root,
        string relative,
        string path,
        CancellationToken cancellationToken)
    {
        var token = _fileSystem.NewAnchorToken();
        var intent = Guid.NewGuid();
        await _evidence.InsertAnchorIntentAsync(intent, root.RootIdentityId, relative, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _fileSystem.AssignAnchorToken(path, token);
        }
        catch
        {
            // The xattr write failed or could not be made durable: take back whatever landed
            // and close the intent, so the row never claims an anchor that is not there.
            _ = _fileSystem.RemoveAnchorTokenIfEquals(path, token);
            await _evidence.SetIntentStateAsync(
                intent,
                PermalinkPathEvidenceStore.IntentAbandoned,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (token, intent);
    }
}
