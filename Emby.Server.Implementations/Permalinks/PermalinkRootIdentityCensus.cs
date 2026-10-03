// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Validates every configured root and every historical registered root before absence can use
/// their generations. Configuration removal never silently shrinks the evidence boundary.
/// </summary>
internal sealed class PermalinkRootIdentityCensus : IPermalinkRootIdentityCensus
{
    private readonly PermalinkContentRoots _contentRoots;
    private readonly PermalinkRootIdentityRegistry _registry;
    private readonly PermalinkRootIdentityStore _store;

    public PermalinkRootIdentityCensus(
        PermalinkContentRoots contentRoots,
        PermalinkRootIdentityRegistry registry,
        PermalinkRootIdentityStore store)
    {
        _contentRoots = contentRoots;
        _registry = registry;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<PermalinkRootIdentityCensusResult> ReadAsync(CancellationToken cancellationToken)
    {
        var generations = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var configuredRoot in _contentRoots.ConfiguredRoots)
        {
            Add(
                generations,
                await _registry.EnsureKnownAsync(configuredRoot, cancellationToken).ConfigureAwait(false));
        }

        foreach (var canonicalRoot in await _store.ListCanonicalRootsAsync(cancellationToken).ConfigureAwait(false))
        {
            Add(
                generations,
                await _registry.EnsureKnownAsync(canonicalRoot, cancellationToken).ConfigureAwait(false));
        }

        return new PermalinkRootIdentityCensusResult(JsonSerializer.Serialize(generations));
    }

    private static void Add(
        IDictionary<string, long> generations,
        PermalinkRootIdentity identity)
    {
        if (generations.TryGetValue(identity.RootIdentityId, out var existing)
            && existing != identity.Generation)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "permalink-root-generation-conflict",
                $"Root identity '{identity.RootIdentityId}' resolved at both generation {existing} and {identity.Generation}.");
        }

        generations[identity.RootIdentityId] = identity.Generation;
    }
}
