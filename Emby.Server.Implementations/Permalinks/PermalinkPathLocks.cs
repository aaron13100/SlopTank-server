// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// A keyed async mutex whose entries are reference counted and removed when unused, so the
/// table never grows with the library. Lock order across callers is always the per-item lock
/// first, then path keys in lexical order; a lease holds exactly one key today.
/// </summary>
internal sealed class PermalinkPathLocks
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Gets the number of keys currently held or awaited.</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits for exclusive ownership of a key.
    /// </summary>
    /// <param name="key">The lock key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handle whose disposal releases the key.</returns>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Unreference(key, entry);
            throw;
        }

        return new Handle(this, key, entry);
    }

    private void Unreference(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _ = _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Handle : IDisposable
    {
        private readonly PermalinkPathLocks _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private int _released;

        public Handle(PermalinkPathLocks owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            _ = _entry.Semaphore.Release();
            _owner.Unreference(_key, _entry);
        }
    }
}
