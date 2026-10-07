// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Freezes exact source and relation successor events before a mutation is claimed.</summary>
internal sealed class PermalinkAggregateSuccessorPlanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkDocumentFactory _documents;

    public PermalinkAggregateSuccessorPlanner(
        ILibraryManager libraryManager,
        PermalinkAuthorityStore authority,
        PermalinkCapsuleStore capsules,
        PermalinkBindingIndex bindings,
        PermalinkEvidence evidence,
        PermalinkDocumentFactory documents)
    {
        _libraryManager = libraryManager;
        _authority = authority;
        _capsules = capsules;
        _bindings = bindings;
        _evidence = evidence;
        _documents = documents;
    }

    public async Task<IReadOnlyList<PermalinkPreparedContentSuccessor>> PrepareAsync(
        PermalinkOperationDocument operation,
        BaseItem source,
        PermalinkEvidenceResult desired,
        CancellationToken cancellationToken)
    {
        var bundle = PermalinkMediaMutation.RequireBundle(operation);
        var claims = bundle.Claims.ToDictionary(value => value.ItemId);
        var items = claims.Keys.ToDictionary(
            id => id,
            id => id.Equals(source.Id)
                ? source
                : _libraryManager.GetItemById(id)
                    ?? throw Conflict("successor-item-missing", $"Claimed item '{id}' is unavailable."));
        var states = new Dictionary<Guid, PermalinkStoredState>
        {
            [source.Id] = new PermalinkStoredState(
                claims[source.Id].CapsuleId,
                desired.ContentRoot,
                [])
        };
        var result = new List<PermalinkPreparedContentSuccessor>(claims.Count);

        await AddSuccessorAsync(source, desired).ConfigureAwait(false);
        foreach (var pair in items
                     .Where(value => !value.Key.Equals(source.Id))
                     .OrderBy(value => Order(value.Value))
                     .ThenBy(value => value.Key))
        {
            var evidence = pair.Value switch
            {
                Season season => _evidence.ComputeDescendantManifest(
                    await GetDescendantsAsync(season, states, cancellationToken).ConfigureAwait(false)),
                Series series => _evidence.ComputeDescendantManifest(
                    await GetDescendantsAsync(series, states, cancellationToken).ConfigureAwait(false)),
                BoxSet boxSet => _evidence.ComputeBoxSetManifest(
                    claims[pair.Key].CapsuleId,
                    await GetMembersAsync(boxSet, states, cancellationToken).ConfigureAwait(false)),
                _ => throw Conflict(
                    "successor-kind-unsupported",
                    $"Claimed aggregate '{pair.Value.GetType().Name}' has no relation manifest.")
            };
            states[pair.Key] = new PermalinkStoredState(
                claims[pair.Key].CapsuleId,
                evidence.ContentRoot,
                []);
            await AddSuccessorAsync(pair.Value, evidence).ConfigureAwait(false);
        }

        return result.OrderBy(value => value.CapsuleId).ToArray();

        async Task AddSuccessorAsync(BaseItem item, PermalinkEvidenceResult evidence)
        {
            var claim = claims[item.Id];
            var reservation = await _authority.FindByCapsuleAsync(claim.CapsuleId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict("capsule-missing", $"Protected capsule '{claim.CapsuleId}' is unknown.");
            var snapshot = await _capsules.ReadValidatedAsync(
                reservation.CapsulePath,
                claim.CapsuleId,
                reservation.ItemKind,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.ContentHead.ContentRoot, claim.Predecessor, StringComparison.Ordinal))
            {
                throw Conflict(
                    "successor-predecessor-mismatch",
                    $"Capsule '{claim.CapsuleId}' no longer has the claimed predecessor.");
            }

            var request = new PermalinkStoreRequest(
                item.Id,
                reservation.ItemKind,
                item.Path,
                evidence.ContentRoot,
                evidence.Leaves,
                true,
                null);
            result.Add(new PermalinkPreparedContentSuccessor(
                claim.CapsuleId,
                item.Id,
                claim.Predecessor,
                _documents.CreateControlledContentSuccessor(request, snapshot, operation.OperationId)));
        }
    }

    /// <summary>
    /// Reconstructs the one safe compatibility shape for operation records created before exact
    /// successors were added: a single source claim. The event identity and timestamp derive only
    /// from immutable operation data so a crash retry recreates byte-identical evidence.
    /// </summary>
    /// <param name="operation">The immutable legacy operation.</param>
    /// <param name="source">The source item.</param>
    /// <param name="desired">The exact observed desired evidence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deterministic source successor.</returns>
    public async Task<IReadOnlyList<PermalinkPreparedContentSuccessor>> PrepareLegacySingleAsync(
        PermalinkOperationDocument operation,
        BaseItem source,
        PermalinkEvidenceResult desired,
        CancellationToken cancellationToken)
    {
        var bundle = PermalinkMediaMutation.RequireBundle(operation);
        if (bundle.Claims.Count != 1
            || bundle.Claims[0] is not { Kind: "content" } claim
            || !claim.ItemId.Equals(source.Id))
        {
            throw Conflict(
                "prepared-successors-missing",
                "A relation mutation without frozen successors cannot be recovered safely.");
        }

        var reservation = await _authority.FindByCapsuleAsync(claim.CapsuleId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict("capsule-missing", $"Protected capsule '{claim.CapsuleId}' is unknown.");
        var snapshot = await _capsules.ReadValidatedAsync(
            reservation.CapsulePath,
            claim.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"legacy-successor\0{operation.OperationId:D}\0{claim.CapsuleId:D}\0{claim.Predecessor}"));
        var eventId = new Guid(identity.AsSpan(0, 16));
        if (snapshot.ContentHead.EventId.Equals(eventId))
        {
            var stored = snapshot.ContentHead;
            var previous = stored.PreviousContentEventId.HasValue
                ? snapshot.Events.SingleOrDefault(value =>
                    value.EventId.Equals(stored.PreviousContentEventId.Value))
                : null;
            if (!stored.OperationId.Equals(operation.OperationId)
                || !string.Equals(stored.ContentRoot, desired.ContentRoot, StringComparison.Ordinal)
                || previous is null
                || !string.Equals(previous.ContentRoot, claim.Predecessor, StringComparison.Ordinal))
            {
                throw Conflict(
                    "legacy-successor-mismatch",
                    "The deterministic legacy successor is present with different evidence.");
            }

            return [new PermalinkPreparedContentSuccessor(
                claim.CapsuleId,
                source.Id,
                claim.Predecessor,
                stored)];
        }

        if (!string.Equals(snapshot.ContentHead.ContentRoot, claim.Predecessor, StringComparison.Ordinal))
        {
            throw Conflict(
                "successor-predecessor-mismatch",
                $"Capsule '{claim.CapsuleId}' no longer has the legacy claimed predecessor.");
        }

        var request = new PermalinkStoreRequest(
            source.Id,
            reservation.ItemKind,
            source.Path,
            desired.ContentRoot,
            desired.Leaves,
            true,
            null);
        var successor = _documents.CreateControlledContentSuccessor(request, snapshot, operation.OperationId);
        successor = successor with
        {
            EventId = eventId,
            CreatedAt = operation.CreatedAt
        };
        return [new PermalinkPreparedContentSuccessor(
            claim.CapsuleId,
            source.Id,
            claim.Predecessor,
            successor)];
    }

    private async Task<IReadOnlyList<(BaseItem Item, PermalinkStoredState State)>> GetDescendantsAsync(
        Folder aggregate,
        IReadOnlyDictionary<Guid, PermalinkStoredState> planned,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<Guid, BaseItem>();
        foreach (var child in aggregate.GetRecursiveChildren().Where(IsContentItem))
        {
            items[child.Id] = child;
            if (child is Video video)
            {
                foreach (var alternateId in _libraryManager.GetLocalAlternateVersionIds(video))
                {
                    if (_libraryManager.GetItemById(alternateId) is Video alternate)
                    {
                        items[alternate.Id] = alternate;
                    }
                }
            }
        }

        var result = new List<(BaseItem, PermalinkStoredState)>(items.Count);
        foreach (var item in items.Values)
        {
            result.Add((item, await GetStateAsync(item, planned, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    private async Task<IReadOnlyList<(BaseItem Item, PermalinkStoredState State)>> GetMembersAsync(
        BoxSet boxSet,
        IReadOnlyDictionary<Guid, PermalinkStoredState> planned,
        CancellationToken cancellationToken)
    {
        var result = new List<(BaseItem, PermalinkStoredState)>();
        foreach (var item in boxSet.GetLinkedChildren())
        {
            result.Add((item, await GetStateAsync(item, planned, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    private async Task<PermalinkStoredState> GetStateAsync(
        BaseItem item,
        IReadOnlyDictionary<Guid, PermalinkStoredState> planned,
        CancellationToken cancellationToken)
    {
        if (planned.TryGetValue(item.Id, out var state))
        {
            return state;
        }

        var bindings = await _bindings.FindItemBindingsAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var identities = bindings.Select(value => (value.CapsuleId, value.ContentRoot)).Distinct().ToArray();
        if (identities.Length != 1)
        {
            throw Conflict(
                "successor-binding-unknown",
                $"Item '{item.Id}' does not have one exact durable relation state.");
        }

        return new PermalinkStoredState(identities[0].CapsuleId, identities[0].ContentRoot, []);
    }

    private static int Order(BaseItem item) => item switch
    {
        Season => 0,
        Series => 1,
        BoxSet => 2,
        _ => 3
    };

    private static bool IsContentItem(BaseItem item)
        => item is not Folder && item.GetBaseItemKind() is not BaseItemKind.Playlist;

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);
}
