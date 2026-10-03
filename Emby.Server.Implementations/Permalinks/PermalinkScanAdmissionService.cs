// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Holds the canonical path leases of new scan candidates through repository creation. A candidate
/// that loses its recheck is represented by durable pending-scan evidence and excluded from the
/// indexing batch; a later successful scan closes that evidence.
/// </summary>
internal sealed class PermalinkScanAdmissionService : IPermalinkScanAdmissionService
{
    private static readonly HashSet<string> _blockingCodes = new(StringComparer.Ordinal)
    {
        "permalink-path-reserved",
        "permalink-path-writer-admitted"
    };

    private readonly PermalinkContentRoots _contentRoots;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkPathLeaseFactory _leases;
    private readonly PermalinkPathEvidenceStore _evidence;
    private int _pendingState = -1;

    public PermalinkScanAdmissionService(
        PermalinkContentRoots contentRoots,
        PermalinkAuthorityStore authority,
        PermalinkPathLeaseFactory leases,
        PermalinkPathEvidenceStore evidence)
    {
        _contentRoots = contentRoots;
        _authority = authority;
        _leases = leases;
        _evidence = evidence;
    }

    /// <inheritdoc />
    public async Task<IPermalinkScanBatchAdmission> BeginNewItemBatchAsync(
        IReadOnlyList<BaseItem> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var managed = new Dictionary<Guid, string>();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path)
                && _contentRoots.TryResolve(candidate.Path, out _))
            {
                managed[candidate.Id] = candidate.Path;
            }
        }

        if (managed.Count == 0 || !_authority.IsConfigured)
        {
            return new BatchAdmission(this, null, _evidence, managed, new HashSet<Guid>());
        }

        var leases = await _leases.AcquireWriterSetAsync(managed.Values, cancellationToken)
            .ConfigureAwait(false);
        var blocked = new HashSet<Guid>();
        try
        {
            foreach (var (itemId, path) in managed)
            {
                try
                {
                    await leases.RecheckScannerAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (PermalinkException exception) when (_blockingCodes.Contains(exception.Code))
                {
                    await _evidence.RecordPendingScanResultAsync(
                        leases.KeyOf(path),
                        cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _pendingState, 1);
                    blocked.Add(itemId);
                }
            }

            return new BatchAdmission(this, leases, _evidence, managed, blocked);
        }
        catch
        {
            await leases.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ReconcileRediscoveredItemsAsync(
        IReadOnlyList<BaseItem> rediscoveredItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rediscoveredItems);
        if (!_authority.IsConfigured
            || rediscoveredItems.Count == 0
            || Volatile.Read(ref _pendingState) == 0)
        {
            return;
        }

        var pendingRelativePaths = await _evidence.ReadPendingScanRelativePathsAsync(cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _pendingState, pendingRelativePaths.Count == 0 ? 0 : 1);
        if (pendingRelativePaths.Count == 0)
        {
            return;
        }

        foreach (var item in rediscoveredItems)
        {
            if (string.IsNullOrWhiteSpace(item.Path)
                || !_contentRoots.TryResolve(item.Path, out var configuredRoot)
                || !pendingRelativePaths.Contains(PermalinkPathKey.Normalize(configuredRoot, item.Path)))
            {
                continue;
            }

            await using var leases = await _leases.AcquireWriterSetAsync([item.Path], cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await leases.RecheckScannerAsync(item.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (PermalinkException exception) when (_blockingCodes.Contains(exception.Code))
            {
                continue;
            }

            await _evidence.CompletePendingScanResultsAsync(
                leases.KeyOf(item.Path),
                cancellationToken).ConfigureAwait(false);
        }

        _ = await HasPendingScanResultsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasPendingScanResultsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _pendingState) == 0)
        {
            return false;
        }

        var pending = await _evidence.HasPendingScanResultsAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _pendingState, pending ? 1 : 0);
        return pending;
    }

    private sealed class BatchAdmission : IPermalinkScanBatchAdmission
    {
        private readonly PermalinkScanAdmissionService _owner;
        private readonly PermalinkWriterLeaseSet? _leases;
        private readonly PermalinkPathEvidenceStore _evidence;
        private readonly IReadOnlyDictionary<Guid, string> _managed;
        private readonly IReadOnlySet<Guid> _blocked;
        private int _completed;

        public BatchAdmission(
            PermalinkScanAdmissionService owner,
            PermalinkWriterLeaseSet? leases,
            PermalinkPathEvidenceStore evidence,
            IReadOnlyDictionary<Guid, string> managed,
            IReadOnlySet<Guid> blocked)
        {
            _owner = owner;
            _leases = leases;
            _evidence = evidence;
            _managed = managed;
            _blocked = blocked;
        }

        /// <inheritdoc />
        public bool IsBlocked(Guid itemId) => _blocked.Contains(itemId);

        /// <inheritdoc />
        public async Task CompleteAsync(
            IReadOnlyList<BaseItem> indexedItems,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(indexedItems);
            if (Interlocked.Exchange(ref _completed, 1) != 0 || _leases is null)
            {
                return;
            }

            var completedKeys = new HashSet<PermalinkLeasedKey>();
            foreach (var item in indexedItems)
            {
                if (_blocked.Contains(item.Id)
                    || !_managed.TryGetValue(item.Id, out var path))
                {
                    continue;
                }

                var key = _leases.KeyOf(path);
                if (completedKeys.Add(key))
                {
                    await _evidence.CompletePendingScanResultsAsync(key, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            _ = await _owner.HasPendingScanResultsAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
            => _leases?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
