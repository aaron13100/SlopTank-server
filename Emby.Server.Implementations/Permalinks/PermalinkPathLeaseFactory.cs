// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-02, 2026-10-03, 2026-10-07.
using System;
using System.Collections.Generic;
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
    private readonly PermalinkContentRoots _contentRoots;

    public PermalinkPathLeaseFactory(
        PermalinkRootIdentityRegistry roots,
        PermalinkPathLocks locks,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects,
        PermalinkPathEvidenceStore evidence,
        PermalinkAuthorityStore authority,
        PermalinkContentRoots contentRoots)
    {
        _roots = roots;
        _locks = locks;
        _fileSystem = fileSystem;
        _objects = objects;
        _evidence = evidence;
        _authority = authority;
        _contentRoots = contentRoots;
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
            var pin = isVirtual ? PermalinkPathLease.VirtualPin : PinContentPath(path);
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

            var existing = await _authority.FindByAnchorAsync(anchor, cancellationToken).ConfigureAwait(false);
            var capsuleId = existing?.CapsuleId ?? Guid.NewGuid();

            return new PermalinkPathLease(
                keyLock,
                path,
                root,
                relative,
                pin,
                anchor,
                capsuleId,
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

    /// <summary>
    /// Orders two lease keys: root identity first, then relative path, both ordinal. The single
    /// definition of lease order, shared by acquisition and the lease set's own iteration.
    /// </summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public static int CompareKeys(PermalinkLeasedKey left, PermalinkLeasedKey right)
    {
        var byRoot = string.CompareOrdinal(left.RootIdentityId, right.RootIdentityId);
        return byRoot != 0
            ? byRoot
            : string.CompareOrdinal(left.NormalizedRelativePath, right.NormalizedRelativePath);
    }

    /// <summary>
    /// Acquires the path leases of a physical writer for every given library path, in ascending
    /// key order (after the caller's per-item lock), and records each key's reservation generation
    /// so <see cref="PermalinkWriterLeaseSet.RecheckAsync"/> can detect a reservation that landed
    /// and was released in between. Duplicate paths (and aliases of one key) are leased once.
    /// Callers can neither pick the order nor forget to release a key: a failure while acquiring
    /// releases every key already taken.
    /// </summary>
    /// <param name="paths">Every library path the writer will create, rename, replace or unlink.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease set; dispose it when the physical steps are done.</returns>
    /// <exception cref="PermalinkException">Ineligible when a path is outside every content root; Unavailable for an UNKNOWN root.</exception>
    public async Task<PermalinkWriterLeaseSet> AcquireWriterSetAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, PermalinkLeasedKey>(StringComparer.Ordinal);
        var byKey = new Dictionary<PermalinkLeasedKey, string>();
        foreach (var path in paths)
        {
            var full = PermalinkWriterLeaseSet.FullPathKey(path);
            if (resolved.ContainsKey(full))
            {
                continue;
            }

            var configuredRoot = _contentRoots.Resolve(path);
            var root = await _roots.EnsureKnownAsync(configuredRoot, cancellationToken).ConfigureAwait(false);
            var key = new PermalinkLeasedKey(root.RootIdentityId, PermalinkPathKey.Normalize(configuredRoot, path));
            resolved[full] = key;
            _ = byKey.TryAdd(key, full);
        }

        var ordered = new List<PermalinkLeasedKey>(byKey.Keys);
        ordered.Sort(CompareKeys);
        var handles = new List<IDisposable>(ordered.Count);
        try
        {
            foreach (var key in ordered)
            {
                handles.Add(await _locks.AcquireAsync(
                    PermalinkPathKey.LockKey(key.RootIdentityId, key.NormalizedRelativePath),
                    cancellationToken).ConfigureAwait(false));
            }

            var generations = new Dictionary<PermalinkLeasedKey, long>();
            await using (var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var key in ordered)
                {
                    generations[key] = await _evidence.ReadMaxReservationGenerationAsync(
                        connection, null, key.RootIdentityId, key.NormalizedRelativePath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var entries = new Dictionary<string, PermalinkWriterLeaseSet.Entry>(StringComparer.Ordinal);
            foreach (var (full, key) in resolved)
            {
                entries[full] = new PermalinkWriterLeaseSet.Entry(key, generations[key]);
            }

            return new PermalinkWriterLeaseSet(entries, handles, _authority, _evidence, _objects);
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Pins the final component without following it and refuses a symlink: the anchor lives on
    /// the object the pin names, so a link would split the pin from the anchor.
    /// </summary>
    private string PinContentPath(string path)
    {
        var pinned = _objects.ReadPinnedObjectNoFollow(path)
            ?? throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "content-unreachable",
                $"Permalink content is unreachable at '{path}'.");
        return pinned.IsSymbolicLink
            ? throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "permalink-path-symlink",
                $"Permalink content path '{path}' is a symbolic link; the final path component is never symlink-resolved.")
            : pinned.Identity;
    }

    private async Task<(string Token, Guid? Intent)> CreateAnchorUnderIntentAsync(
        PermalinkRootIdentity root,
        string relative,
        string path,
        CancellationToken cancellationToken)
    {
        var token = _fileSystem.NewAnchorToken();
        var intent = Guid.NewGuid();
        await _evidence.InsertAnchorIntentAsync(intent, root.RootIdentityId, relative, token, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _fileSystem.AssignAnchorToken(path, token);
        }
        catch
        {
            // The xattr write failed or could not be made durable: take back whatever landed
            // and close the intent, so the row never claims an anchor that is not there.
            _ = await new PermalinkAnchorIntentSettlement(_evidence, _fileSystem, _objects)
                .AbandonAsync(intent, path, token, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return (token, intent);
    }
}
