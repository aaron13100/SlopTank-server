// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-08-03, 2026-09-02, 2026-09-08, 2026-09-09, 2026-09-16, 2026-10-14, 2026-10-31, 2026-11-02, 2026-11-03.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Adapts the existing candidate/lease HTTP API to the portable logical mapping.
/// Leases retain user, purpose, expiry and single-use checks in the application
/// database. They never establish or verify a physical media identity.
/// </summary>
internal sealed class PermalinkResolutionService : IPermalinkResolutionService
{
    private readonly PermalinkManager _manager;
    private readonly MediaPermalinkLeaseRepository _leases;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly TimeProvider _clock;

    public PermalinkResolutionService(
        PermalinkManager manager,
        MediaPermalinkLeaseRepository leases,
        ILibraryManager library,
        IUserManager users,
        TimeProvider clock)
    {
        _manager = manager;
        _leases = leases;
        _library = library;
        _users = users;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId, string purpose, Guid userId, CancellationToken cancellationToken)
    {
        if (purpose is not "details" and not "playback")
        {
            throw Conflict("purpose-invalid", $"Unknown permalink purpose '{purpose}'.");
        }

        var candidates = await _manager.GetVideoFileAsync(permalinkId, userId, cancellationToken).ConfigureAwait(false);
        var result = new List<PermalinkCandidateEnvelope>(candidates.Count);
        foreach (var candidate in candidates.OrderBy(value => value.ContentId))
        {
            var issued = CreateLease(new LeaseIdentity(permalinkId, candidate.ContentId, userId, purpose));
            await _leases.InsertAsync(issued.Document, cancellationToken).ConfigureAwait(false);
            result.Add(Envelope(issued, result.Count));
        }

        return result;
    }

    public async Task<BaseItem> RedeemDetailsAsync(
        string handle, string lease, Guid userId, CancellationToken cancellationToken)
    {
        var document = await ReadLeaseAsync(new LeaseCredentials(handle, lease, userId, "details"), cancellationToken).ConfigureAwait(false);
        RequireUnconsumed(document);
        var candidate = await ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        document.ConsumedAtUtc = Now;
        document.UpdatedAtUtc = Now;
        await _leases.SaveAsync(document, null, cancellationToken).ConfigureAwait(false);
        return Representative(candidate);
    }

    public async Task<PermalinkCandidateEnvelope> ExchangePlaybackLeaseAsync(
        string handle, string lease, Guid userId, CancellationToken cancellationToken, bool clientHeldLease = true)
    {
        var document = await ReadLeaseAsync(new LeaseCredentials(handle, lease, userId, "details"), cancellationToken).ConfigureAwait(false);
        RequireUnconsumed(document);
        await ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        var issued = CreateLease(new LeaseIdentity(document.Alias, document.ContentId, userId, "playback"));
        document.ConsumedAtUtc = Now;
        document.UpdatedAtUtc = Now;
        // Both client-held and server-held successors use the same durable DB transaction.
        await _leases.SaveAsync(document, issued.Document, cancellationToken).ConfigureAwait(false);
        return Envelope(issued, 0);
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackReadyV1Async(
        string handle, string lease, string playbackSessionId, int queueOrdinal, bool complete,
        Guid userId, CancellationToken cancellationToken)
    {
        var playback = await ExchangePlaybackLeaseAsync(handle, lease, userId, cancellationToken).ConfigureAwait(false);
        return await RedeemPlaybackAsync(playback.Handle, playback.Lease, playbackSessionId,
            queueOrdinal, complete, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle, string lease, string playbackSessionId, int queueOrdinal, bool complete,
        Guid userId, CancellationToken cancellationToken, bool clientHeldLease = true)
    {
        if (string.IsNullOrWhiteSpace(playbackSessionId) || playbackSessionId.Length > 128)
        {
            throw Conflict("playback-session-required", "A playback session of at most 128 characters is required.");
        }

        var document = await ReadLeaseAsync(new LeaseCredentials(handle, lease, userId, "playback"), cancellationToken).ConfigureAwait(false);
        var candidate = await ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        PlaybackQueue plan;
        if (document.ConsumedAtUtc is null)
        {
            if (queueOrdinal != 0)
            {
                throw Conflict("playback-ordinal-skipped", "The first playback redemption must consume queue ordinal 0.");
            }

            plan = await BuildPlanAsync(Representative(candidate), userId, cancellationToken).ConfigureAwait(false);
            document.PlaybackSessionId = playbackSessionId;
            document.PlaybackPlan = JsonSerializer.Serialize(plan);
            document.ConsumedAtUtc = Now;
        }
        else
        {
            if (!string.Equals(document.PlaybackSessionId, playbackSessionId, StringComparison.Ordinal))
            {
                throw Conflict("playback-lease-consumed", "The playback lease was consumed by another session.");
            }

            plan = ReadPlan(document);
        }

        if (queueOrdinal < 0 || queueOrdinal >= plan.Entries.Count)
        {
            throw Conflict("playback-ordinal-invalid", "The playback queue ordinal is outside this session's queue.");
        }

        if (complete && queueOrdinal != plan.Entries.Count - 1)
        {
            throw Conflict("playback-complete-early", "Playback cannot complete before the final queue entry.");
        }

        if (document.CompletedAtUtc.HasValue && (!complete || queueOrdinal != plan.Entries.Count - 1))
        {
            throw Conflict("playback-lease-consumed", "This playback session has already completed.");
        }

        var entry = plan.Entries[queueOrdinal];
        var visible = await _manager.GetVideoFileAsync(MediaContentIdentifier.OpaqueAlias(entry.ContentId), userId, cancellationToken).ConfigureAwait(false);
        var version = visible.SingleOrDefault(value => value.ContentId == entry.ContentId)
            ?? throw Conflict("playback-item-unavailable", "This queued title is no longer available to the current user.");
        var item = version.Items.FirstOrDefault(value => value.Id == entry.ItemId) ?? Representative(version);
        var paths = item is Video video
            ? new[] { item.Path }.Concat(video.AdditionalParts).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (paths.Length == 0)
        {
            throw Conflict("playback-source-missing", $"Library item '{item.Id}' has no current media source.");
        }

        document.UpdatedAtUtc = Now;
        if (complete)
        {
            document.CompletedAtUtc ??= Now;
        }

        await _leases.SaveAsync(document, null, cancellationToken).ConfigureAwait(false);
        // Existing v1 field names are retained. Paths come from normal library
        // metadata; the ordinary playback endpoint owns file access and delivery.
        return new PermalinkPlaybackSnapshot(item.Id, paths[0], plan.Entries.Count, playbackSessionId, paths);
    }

    private async Task<MediaPermalinkLease> ReadLeaseAsync(LeaseCredentials credentials, CancellationToken token)
    {
        if (!IsToken(credentials.Handle) || !IsToken(credentials.Secret))
        {
            throw Conflict("lease-invalid", "The permalink lease is invalid.");
        }

        var document = await _leases.ReadAsync(credentials.Handle, token).ConfigureAwait(false);
        if (document is null || document.UserId != credentials.UserId || document.Kind != credentials.Purpose
            || document.DeletedAtUtc.HasValue || document.ExpiresAtUtc <= Now
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(document.SecretDigest),
                Encoding.UTF8.GetBytes(MediaPermalinkLeaseRepository.Digest(credentials.Secret))))
        {
            throw Conflict("lease-invalid", "The permalink lease is invalid or has expired.");
        }

        if (document.RecordVersion != 1)
        {
            throw new PermalinkException(
                PermalinkErrorKind.VersionRefused,
                "lease-version-unsupported",
                $"Permalink lease format {document.RecordVersion} is not supported.");
        }

        return document;
    }

    private async Task<PermalinkManager.Candidate> ResolveAsync(MediaPermalinkLease lease, CancellationToken token)
    {
        var candidates = await _manager.GetVideoFileAsync(lease.Alias, lease.UserId, token).ConfigureAwait(false);
        return candidates.SingleOrDefault(value => value.ContentId == lease.ContentId)
            ?? throw Conflict("lease-evidence-changed", "The linked title is no longer available to this user under this identifier.");
    }

    private async Task<PlaybackQueue> BuildPlanAsync(BaseItem item, Guid userId, CancellationToken token)
    {
        var user = _users.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            throw Conflict("lease-invalid", "The permalink user is unavailable or disabled.");
        }

        IEnumerable<BaseItem> selected = item switch
        {
            Series or Season => ((Folder)item).GetRecursiveChildren(user, new InternalItemsQuery(user), out _)
                .OfType<Video>().OrderBy(value => value.ParentIndexNumber).ThenBy(value => value.IndexNumber).ThenBy(value => value.SortName),
            BoxSet boxSet => boxSet.GetLinkedChildren().OfType<Video>(),
            _ => new[] { item }
        };
        var entries = new List<QueueEntry>();
        foreach (var child in selected.Take(100))
        {
            token.ThrowIfCancellationRequested();
            var visible = _library.GetItemById<BaseItem>(child.Id, user);
            if (visible is not Video || visible.ExtraType.HasValue)
            {
                continue;
            }

            await _manager.EnsurePermalinkIdsAsync(visible, token).ConfigureAwait(false);
            entries.Add(new QueueEntry(visible.Id, visible.PermalinkContentId!.Value));
        }

        if (entries.Count == 0)
        {
            throw Conflict("playback-plan-empty", $"Item '{item.Id}' has no visible playable entries.");
        }

        return new PlaybackQueue(1, entries);
    }

    private IssuedLease CreateLease(LeaseIdentity identity)
    {
        var secret = MediaPermalinkLeaseRepository.NewSecret();
        var now = Now;
        return new IssuedLease(
            new MediaPermalinkLease
            {
                Handle = MediaPermalinkLeaseRepository.NewSecret(),
                SecretDigest = MediaPermalinkLeaseRepository.Digest(secret),
                Alias = identity.Alias,
                ContentId = identity.ContentId,
                UserId = identity.UserId,
                Kind = identity.Purpose,
                ExpiresAtUtc = now.AddMinutes(5),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            secret);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static BaseItem Representative(PermalinkManager.Candidate candidate) => candidate.Items
        .OrderBy(item => item is Video { PrimaryVersionId: not null } ? 1 : 0).ThenBy(item => item.Id).First();

    private static PermalinkCandidateEnvelope Envelope(IssuedLease issued, int rank) => new(
        rank,
        issued.Document.Alias.StartsWith("sk-", StringComparison.Ordinal) ? "sloptank" : "external",
        issued.Document.Handle,
        issued.Secret);

    private static bool IsToken(string value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void RequireUnconsumed(MediaPermalinkLease lease)
    {
        if (lease.ConsumedAtUtc.HasValue)
        {
            throw Conflict("lease-consumed", "The permalink lease has already been consumed.");
        }
    }

    private static PlaybackQueue ReadPlan(MediaPermalinkLease document)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<PlaybackQueue>(document.PlaybackPlan ?? "null");
            return plan is { Version: 1, Entries.Count: > 0 and <= 100 } && plan.Entries.All(entry => entry.ContentId != Guid.Empty && entry.ItemId != Guid.Empty)
                ? plan : throw new JsonException("The stored logical queue is missing or unsupported.");
        }
        catch (JsonException exception)
        {
            throw new PermalinkException(PermalinkErrorKind.Corrupt, "playback-plan-invalid", exception.Message, exception);
        }
    }

    private static PermalinkException Conflict(string code, string message) => new(PermalinkErrorKind.Conflict, code, message);

    private sealed record LeaseIdentity(string Alias, Guid ContentId, Guid UserId, string Purpose);
    private sealed record LeaseCredentials(string Handle, string Secret, Guid UserId, string Purpose);
    private sealed record IssuedLease(MediaPermalinkLease Document, string Secret);
    private sealed record QueueEntry(Guid ItemId, Guid ContentId);
    private sealed record PlaybackQueue(int Version, IReadOnlyList<QueueEntry> Entries);
}
