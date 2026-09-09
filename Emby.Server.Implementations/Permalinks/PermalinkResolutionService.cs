// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-08-03, 2026-09-02, 2026-09-08, 2026-09-09.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Revalidates physical and logical identity before metadata or playback leaves the server.
/// </summary>
internal sealed class PermalinkResolutionService : IPermalinkResolutionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPermalinkManager _manager;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkLeaseStore _leases;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkPlaybackDocumentStore _playbackDocuments;
    private readonly PermalinkPlaybackStateStore _playback;

    public PermalinkResolutionService(
        ILibraryManager libraryManager,
        IPermalinkManager manager,
        PermalinkBindingIndex bindings,
        PermalinkLeaseStore leases,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkOperationJournal journal,
        PermalinkPlaybackDocumentStore playbackDocuments,
        PermalinkPlaybackStateStore playback)
    {
        _libraryManager = libraryManager;
        _manager = manager;
        _bindings = bindings;
        _leases = leases;
        _fileSystem = fileSystem;
        _journal = journal;
        _playbackDocuments = playbackDocuments;
        _playback = playback;
    }

    public async Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (purpose is not "details" and not "playback")
        {
            throw Conflict("purpose-invalid", $"Unknown permalink purpose '{purpose}'.");
        }

        if (purpose == "playback")
        {
            // Retention is request-triggered so it cannot create load while the store is idle.
            // The reclaimer is disabled by default and every enabled pass has a hard entry cap.
            await _playback.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }

        var candidates = await _bindings.FindResolutionBindingsAsync(
            permalinkId,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0
            && await _bindings.IsKnownAliasAsync(permalinkId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Conflict(
                "alias-detached",
                $"Permalink '{permalinkId}' has no verified live binding.");
        }

        var result = new List<PermalinkCandidateEnvelope>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var verified = await VerifyAsync(candidate, null, cancellationToken).ConfigureAwait(false);
            var envelope = await _leases.IssueAsync(
                candidate,
                verified.Evidence,
                purpose,
                userId,
                cancellationToken).ConfigureAwait(false);
            if (purpose == "playback")
            {
                await _playbackDocuments.PublishLeaseAsync(envelope.Handle, cancellationToken)
                    .ConfigureAwait(false);
            }

            result.Add(envelope);
        }

        if (result.Count == 0 && IsExternalAlias(permalinkId))
        {
            throw Conflict(
                "EvidenceRequired",
                "EvidenceRequired: initialize protected evidence from the authenticated local item page.");
        }

        return result;
    }

    public async Task<BaseItem> RedeemDetailsAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var document = await _leases.ConsumeAsync(
            handle,
            lease,
            "details",
            userId,
            cancellationToken).ConfigureAwait(false);
        var binding = await _bindings.FindResolutionBindingAsync(
            document.PermalinkId,
            document.ItemId,
            cancellationToken).ConfigureAwait(false);
        return (await VerifyAsync(binding, document, cancellationToken).ConfigureAwait(false)).Item;
    }

    public async Task<PermalinkCandidateEnvelope> ExchangePlaybackLeaseAsync(
        string handle,
        string lease,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var document = await _leases.ConsumeAsync(
            handle,
            lease,
            "details",
            userId,
            cancellationToken).ConfigureAwait(false);
        var binding = await _bindings.FindResolutionBindingAsync(
            document.PermalinkId,
            document.ItemId,
            cancellationToken).ConfigureAwait(false);
        var verified = await VerifyAsync(binding, document, cancellationToken).ConfigureAwait(false);
        var envelope = await _leases.IssueAsync(
            binding,
            verified.Evidence,
            "playback",
            userId,
            cancellationToken,
            handle).ConfigureAwait(false);
        await _playback.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await _playbackDocuments.PublishLeaseAsync(envelope.Handle, cancellationToken)
            .ConfigureAwait(false);
        return envelope;
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle,
        string lease,
        string playbackSessionId,
        int queueOrdinal,
        bool complete,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await _playback.RecoverAsync(cancellationToken).ConfigureAwait(false);
        var continuation = _playback.IsActiveSession(handle, playbackSessionId, userId);
        var document = await _leases.ValidatePlaybackAsync(
            handle,
            lease,
            userId,
            continuation,
            cancellationToken).ConfigureAwait(false);
        var binding = await _bindings.FindResolutionBindingAsync(
            document.PermalinkId,
            document.ItemId,
            cancellationToken).ConfigureAwait(false);
        var item = (await VerifyAsync(binding, document, cancellationToken).ConfigureAwait(false)).Item;
        var entries = BuildPlaybackPlan(item);
        var snapshot = await _playback.AdmitAsync(
            handle,
            playbackSessionId,
            userId,
            document.ServerId,
            entries,
            queueOrdinal,
            complete,
            cancellationToken).ConfigureAwait(false);
        if (!continuation)
        {
            await _leases.MarkPlaybackConsumedAsync(document, cancellationToken)
                .ConfigureAwait(false);
        }

        return snapshot;
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackReadyV1Async(
        string handle,
        string lease,
        string playbackSessionId,
        int queueOrdinal,
        bool complete,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var playbackCandidate = await ExchangePlaybackLeaseAsync(
            handle,
            lease,
            userId,
            cancellationToken).ConfigureAwait(false);
        return await RedeemPlaybackAsync(
            playbackCandidate.Handle,
            playbackCandidate.Lease,
            playbackSessionId,
            queueOrdinal,
            complete,
            userId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VerifiedCandidate> VerifyAsync(
        PermalinkResolutionBinding binding,
        PermalinkLeaseDocument? lease,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById<BaseItem>(binding.ItemId)
            ?? throw Conflict("binding-item-missing", $"Bound item '{binding.ItemId}' is missing.");
        if (await _journal.HasPendingAsync(item.Id, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "identity-mutation-pending",
                $"Item '{item.Id}' has an unfinished durable identity mutation.");
        }

        var aliases = await _manager.EnsurePermalinkIdsAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!binding.IsCapsuleOverride
            && !aliases.Ids.Contains(binding.PermalinkId, StringComparer.Ordinal))
        {
            throw Conflict("assignment-mismatch", "The requested alias is not active for this assignment.");
        }

        var anchor = _fileSystem.ReadAnchorToken(item.Path);
        if (!string.Equals(anchor, binding.AnchorToken, StringComparison.Ordinal)
            || !string.Equals(item.Path, binding.CurrentPath, StringComparison.Ordinal))
        {
            throw Conflict("binding-replaced", "The physical binding or stable anchor changed.");
        }

        if (!binding.PermalinkId.StartsWith("sk-", StringComparison.Ordinal)
            && !CurrentExternalAliases(item).Contains(binding.PermalinkId, StringComparer.Ordinal))
        {
            throw Conflict("assignment-mismatch", "Current provider assignment does not accept this alias.");
        }

        var externalAliases = CurrentExternalAliases(item)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var activeAliases = aliases.Ids.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var providerDigest = CanonicalJson.Digest(CanonicalJson.Serialize(externalAliases));
        var aliasDigest = CanonicalJson.Digest(CanonicalJson.Serialize(activeAliases));
        var assignmentHead = CanonicalJson.Digest(CanonicalJson.Serialize(
            new[] { providerDigest, aliasDigest }));
        var evidence = new PermalinkLeaseStore.LeaseEvidence(
            ObjectIdentity(item.Path),
            assignmentHead,
            providerDigest,
            aliasDigest);
        if (lease is not null
            && (!lease.CapsuleId.Equals(binding.CapsuleId)
                || !lease.BindingInstanceId.Equals(binding.BindingInstanceId)
                || !string.Equals(lease.ContentRoot, binding.ContentRoot, StringComparison.Ordinal)
                || !string.Equals(lease.AnchorToken, binding.AnchorToken, StringComparison.Ordinal)
                || !string.Equals(lease.CurrentPath, binding.CurrentPath, StringComparison.Ordinal)
                || !string.Equals(lease.ObjectIdentity, evidence.ObjectIdentity, StringComparison.Ordinal)
                || !string.Equals(lease.AssignmentHead, evidence.AssignmentHead, StringComparison.Ordinal)
                || !string.Equals(
                    lease.AcceptedProviderDigest,
                    evidence.AcceptedProviderDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    lease.ActiveAliasSetDigest,
                    evidence.ActiveAliasSetDigest,
                    StringComparison.Ordinal)))
        {
            throw Conflict(
                "lease-evidence-changed",
                "The binding, assignment head, active aliases, provider claim, or live object changed.");
        }

        return new VerifiedCandidate(item, evidence);
    }

    private static IReadOnlyList<PermalinkPlaybackPlanStore.PlaybackPlanEntry> BuildPlaybackPlan(
        BaseItem item)
    {
        var selected = item switch
        {
            Series or Season => ((Folder)item).GetRecursiveChildren()
                .Where(IsPlayable)
                .Take(100),
            BoxSet boxSet => boxSet.GetLinkedChildren().Where(IsPlayable),
            _ => new[] { item }
        };
        var entries = new List<PermalinkPlaybackPlanStore.PlaybackPlanEntry>();
        foreach (var selectedItem in selected)
        {
            var sources = BuildSources(selectedItem);
            if (sources.Count > 0)
            {
                entries.Add(new PermalinkPlaybackPlanStore.PlaybackPlanEntry(selectedItem.Id, sources));
            }
        }

        if (entries.Count == 0)
        {
            throw Conflict("playback-plan-empty", $"Item '{item.Id}' has no playable frozen plan.");
        }

        return entries;
    }

    private static IReadOnlyList<PermalinkPlaybackPlanStore.PlaybackSource> BuildSources(
        BaseItem item)
    {
        var paths = new List<(string Path, string RelativePath)>();
        if (item is Video { VideoType: VideoType.Dvd or VideoType.BluRay })
        {
            var mediaRoot = Directory.Exists(Path.Combine(item.Path, "BDMV"))
                ? Path.Combine(item.Path, "BDMV")
                : Path.Combine(item.Path, "VIDEO_TS");
            paths.AddRange(Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => (
                    value,
                    Path.GetRelativePath(item.Path, value).Replace(
                        Path.DirectorySeparatorChar,
                        '/'))));
        }
        else if (item is Video video && !string.IsNullOrEmpty(item.Path))
        {
            paths.Add((item.Path, Path.GetFileName(item.Path)));
            paths.AddRange(video.AdditionalParts.Select(value => (value, Path.GetFileName(value))));
        }

        var sources = new List<PermalinkPlaybackPlanStore.PlaybackSource>(paths.Count);
        foreach (var (path, relativePath) in paths)
        {
            sources.Add(new PermalinkPlaybackPlanStore.PlaybackSource(
                path,
                relativePath,
                PermalinkObjectIdentity.Read(path)
                    ?? throw Conflict(
                        "playback-source-replaced",
                        $"Playback source '{path}' is missing.")));
        }

        return sources;
    }

    private static string ObjectIdentity(string path)
    {
        return PermalinkObjectIdentity.Read(path)
            ?? throw Conflict("binding-item-missing", $"Bound path '{path}' is missing.");
    }

    private static bool IsExternalAlias(string permalinkId)
    {
        if (permalinkId.StartsWith("tt", StringComparison.Ordinal)
            && permalinkId.Length > 2
            && permalinkId.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return true;
        }

        var separator = permalinkId.LastIndexOf('-');
        return (permalinkId.StartsWith("tm-", StringComparison.Ordinal)
                || permalinkId.StartsWith("tv-", StringComparison.Ordinal))
            && separator > 3
            && separator < permalinkId.Length - 1
            && permalinkId.AsSpan(separator + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static IEnumerable<string> CurrentExternalAliases(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue("Imdb", out var imdb))
        {
            yield return imdb;
        }

        if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb))
        {
            var qualifier = item is MediaBrowser.Controller.Entities.Movies.Movie ? "mv"
                : item is Series ? "tv"
                : item is MediaBrowser.Controller.Entities.TV.Episode ? "ep"
                : item is Season ? "se"
                : "co";
            yield return $"tm-{qualifier}-{tmdb}";
        }

        if (item.ProviderIds.TryGetValue("Tvdb", out var tvdb))
        {
            var qualifier = item is MediaBrowser.Controller.Entities.Movies.Movie ? "mv"
                : item is Series ? "tv"
                : item is MediaBrowser.Controller.Entities.TV.Episode ? "ep"
                : item is Season ? "se"
                : "co";
            yield return $"tv-{qualifier}-{tvdb}";
        }
    }

    private static bool IsPlayable(BaseItem item)
    {
        return item is Video;
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }

    private sealed record VerifiedCandidate(BaseItem Item, PermalinkLeaseStore.LeaseEvidence Evidence);
}
