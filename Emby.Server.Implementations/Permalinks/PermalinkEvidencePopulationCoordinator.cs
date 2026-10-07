// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using PopulationEvidence = Emby.Server.Implementations.Permalinks.PermalinkEvidenceRegistryStore.PermalinkPopulationEvidence;
using PopulationSource = Emby.Server.Implementations.Permalinks.PermalinkEvidenceRegistryStore.PermalinkPopulationSource;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Populates physical evidence behind durable per-source cursors and seals only across stable
/// source and root censuses. Replaying a row before its cursor advances is deliberately safe.
/// </summary>
internal sealed class PermalinkEvidencePopulationCoordinator
{
    private readonly PermalinkEvidenceRegistryStore _store;
    private readonly IPermalinkEvidenceSourceCensus _sourceCensus;
    private readonly IPermalinkRootIdentityCensus _rootCensus;

    /// <summary>Initializes a new instance of the <see cref="PermalinkEvidencePopulationCoordinator"/> class.</summary>
    /// <param name="store">The durable registry and checkpoint store.</param>
    /// <param name="sourceCensus">The pinned physical source reader.</param>
    /// <param name="rootCensus">The complete root reattestation reader.</param>
    public PermalinkEvidencePopulationCoordinator(
        PermalinkEvidenceRegistryStore store,
        IPermalinkEvidenceSourceCensus sourceCensus,
        IPermalinkRootIdentityCensus rootCensus)
    {
        _store = store;
        _sourceCensus = sourceCensus;
        _rootCensus = rootCensus;
    }

    /// <summary>Runs or resumes one exact physical-source population.</summary>
    /// <param name="populationId">The stable population identifier.</param>
    /// <param name="requests">The complete physical source plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the population.</returns>
    public async Task RunAsync(
        string populationId,
        IReadOnlyCollection<PermalinkEvidenceSourceCensus.Request> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("A population requires at least one source.", nameof(requests));
        }

        var rootsBefore = await _rootCensus.ReadAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<Snapshot>(requests.Count);
        foreach (var request in requests.OrderBy(SourceSortKey, StringComparer.Ordinal))
        {
            var result = await _sourceCensus.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            snapshots.Add(new Snapshot(request, result));
        }

        var sources = snapshots
            .Select(snapshot => Source(snapshot.Request, snapshot.Result.Generation))
            .ToArray();
        await _store.BeginAsync(populationId, sources, cancellationToken).ConfigureAwait(false);

        foreach (var snapshot in snapshots)
        {
            await PopulateSourceAsync(populationId, snapshot, cancellationToken).ConfigureAwait(false);
        }

        foreach (var snapshot in snapshots)
        {
            var after = await _sourceCensus.ReadAsync(snapshot.Request, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.Result.Generation, after.Generation, StringComparison.Ordinal))
            {
                throw Conflict(
                    "registry-population-source-changed",
                    $"Population source '{snapshot.Request.Key}' changed before the population could be sealed.");
            }
        }

        var rootsAfter = await _rootCensus.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(rootsBefore.RootGenerationsJson, rootsAfter.RootGenerationsJson, StringComparison.Ordinal))
        {
            throw Conflict(
                "registry-population-roots-changed",
                "The attested root generation set changed before the population could be sealed.");
        }

        await _store.CompleteAsync(populationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PopulateSourceAsync(
        string populationId,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var source = Source(snapshot.Request, snapshot.Result.Generation);
        var checkpoint = await _store.ReadCheckpointAsync(populationId, source, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict(
                "registry-population-checkpoint-missing",
                $"Population source '{source.Key}' has no durable checkpoint.");
        if (checkpoint.CursorVersion != 1)
        {
            throw new PermalinkException(
                PermalinkErrorKind.VersionRefused,
                "registry-population-cursor-version",
                $"Population source '{source.Key}' has unsupported cursor version {checkpoint.CursorVersion}.");
        }

        if (checkpoint.Complete)
        {
            return;
        }

        var entries = snapshot.Result.Entries
            .OrderBy(entry => entry.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var start = 0;
        if (checkpoint.Cursor is not null)
        {
            start = Array.FindIndex(
                entries,
                entry => string.Equals(entry.SourceKey, checkpoint.Cursor, StringComparison.Ordinal));
            if (start < 0)
            {
                throw Conflict(
                    "registry-population-cursor-invalid",
                    $"Population source '{source.Key}' no longer contains cursor '{checkpoint.Cursor}'.");
            }

            start++;
        }

        var cursor = checkpoint.Cursor;
        var observed = checkpoint.ObservedCount;
        for (var index = start; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            await _store.RecordEvidenceAsync(
                    populationId,
                    new PopulationEvidence(
                        entry.Kind,
                        snapshot.Request.RootIdentityId,
                        entry.NormalizedRelativePath,
                        entry.SourceKey,
                        entry.OperationId,
                        entry.PinnedObjectIdentity,
                        entry.ContentLength,
                        entry.ContentSha256),
                    cancellationToken)
                .ConfigureAwait(false);
            observed++;
            await _store.AdvanceCheckpointAsync(
                    populationId,
                    source,
                    cursor,
                    entry.SourceKey,
                    observed,
                    complete: index == entries.Length - 1,
                    cancellationToken)
                .ConfigureAwait(false);
            cursor = entry.SourceKey;
        }

        if (entries.Length == 0)
        {
            await _store.AdvanceCheckpointAsync(
                    populationId,
                    source,
                    cursor,
                    "empty:" + snapshot.Result.Generation,
                    observed,
                    complete: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static PopulationSource Source(PermalinkEvidenceSourceCensus.Request request, string generation)
        => new(request.Kind, request.Key, generation);

    private static string SourceSortKey(PermalinkEvidenceSourceCensus.Request request)
        => request.Kind.ToString() + "\0" + request.Key;

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private sealed record Snapshot(
        PermalinkEvidenceSourceCensus.Request Request,
        PermalinkEvidenceSourceCensus.Result Result);
}
