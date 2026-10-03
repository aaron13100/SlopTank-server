// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The path leases a physical writer holds for every library path it creates, renames, replaces
/// or unlinks. Created only by <see cref="PermalinkPathLeaseFactory.AcquireWriterSetAsync"/>, which
/// takes the keys in ordinal order so two multi-path writers can never deadlock. Before each
/// physical publication step the writer calls <see cref="RecheckAsync"/> for the paths that step
/// touches; a lost recheck is a typed Conflict and the writer publishes nothing.
/// </summary>
internal sealed class PermalinkWriterLeaseSet : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, Entry> _entries;
    private readonly IReadOnlyList<IDisposable> _handles;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkPathEvidenceStore _evidence;
    private int _disposed;

    public PermalinkWriterLeaseSet(
        IReadOnlyDictionary<string, Entry> entries,
        IReadOnlyList<IDisposable> handles,
        PermalinkAuthorityStore authority,
        PermalinkPathEvidenceStore evidence)
    {
        _entries = entries;
        _handles = handles;
        _authority = authority;
        _evidence = evidence;
    }

    /// <summary>Gets the leased keys in the order they were acquired.</summary>
    public IReadOnlyList<PermalinkLeasedKey> Keys
    {
        get
        {
            var keys = new List<PermalinkLeasedKey>();
            foreach (var entry in _entries.Values)
            {
                keys.Add(entry.Key);
            }

            keys.Sort(PermalinkPathLeaseFactory.CompareKeys);
            return keys;
        }
    }

    /// <summary>
    /// Returns the lease key of a leased path.
    /// </summary>
    /// <param name="path">The leased path.</param>
    /// <returns>The key.</returns>
    /// <exception cref="PermalinkException">Conflict <c>permalink-writer-path-not-leased</c> when the path is not in the set.</exception>
    public PermalinkLeasedKey KeyOf(string path) => Require(path).Key;

    /// <summary>
    /// Rechecks one leased key against the authority registry immediately before a physical step.
    /// </summary>
    /// <param name="path">A path in the set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    /// <exception cref="PermalinkException">Conflict with the same <c>permalink-*</c> codes Ensure uses when the key changed.</exception>
    public Task RecheckAsync(string path, CancellationToken cancellationToken)
        => RecheckEntriesAsync(new[] { Require(path) }, scanner: false, cancellationToken);

    /// <summary>
    /// Rechecks a new-item scanner key without consuming or rejecting a pending supersession slot.
    /// Reservations and blocking writer admissions still refuse it.
    /// </summary>
    /// <param name="path">A path in the set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    public Task RecheckScannerAsync(string path, CancellationToken cancellationToken)
        => RecheckEntriesAsync(new[] { Require(path) }, scanner: true, cancellationToken);

    /// <summary>
    /// Rechecks every leased key, in key order.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the recheck.</returns>
    /// <exception cref="PermalinkException">Conflict as <see cref="RecheckAsync"/>.</exception>
    public async Task RecheckAllAsync(CancellationToken cancellationToken)
    {
        var ordered = new List<Entry>(_entries.Values);
        ordered.Sort((left, right) => PermalinkPathLeaseFactory.CompareKeys(left.Key, right.Key));
        await RecheckEntriesAsync(ordered, scanner: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases every key.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    internal static string FullPathKey(string path) => Path.GetFullPath(path);

    private Entry Require(string path)
    {
        return _entries.TryGetValue(FullPathKey(path), out var entry)
            ? entry
            : throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "permalink-writer-path-not-leased",
                $"Path '{path}' was not leased by this writer before it was touched.");
    }

    private async Task RecheckEntriesAsync(
        IReadOnlyList<Entry> entries,
        bool scanner,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var entry in entries)
        {
            if (scanner)
            {
                await PermalinkPathKeyRecheck.RequireScannerClearAsync(
                    _evidence,
                    connection,
                    transaction,
                    entry.Key.RootIdentityId,
                    entry.Key.NormalizedRelativePath,
                    entry.Generation,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PermalinkPathKeyRecheck.RequireClearAsync(
                    _evidence,
                    connection,
                    transaction,
                    entry.Key.RootIdentityId,
                    entry.Key.NormalizedRelativePath,
                    entry.Generation,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One leased path: its key and the reservation generation read when the lease was taken.</summary>
    /// <param name="Key">The lease key.</param>
    /// <param name="Generation">The highest reservation generation for the key at acquire.</param>
    internal sealed record Entry(PermalinkLeasedKey Key, long Generation);
}
