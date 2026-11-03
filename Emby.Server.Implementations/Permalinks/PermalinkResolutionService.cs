// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-08-03, 2026-09-02, 2026-09-08, 2026-09-09, 2026-09-16, 2026-10-14, 2026-10-31, 2026-11-02, 2026-11-03.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Adapts the existing candidate/lease HTTP API to the portable logical mapping.
/// Leases retain user, purpose, expiry and single-use checks in the application
/// database. They never establish or verify a physical media identity.
/// </summary>
internal sealed class PermalinkResolutionService : IPermalinkResolutionService
{
    private static readonly SearchValues<char> _tokenCharacters = SearchValues.Create("0123456789abcdef");

    private readonly PermalinkManager _manager;
    private readonly MediaPermalinkLeaseRepository _leases;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PermalinkResolutionService> _logger;
    private readonly Guid _instanceId = Guid.NewGuid();
    private int _reclamationOffset;

    public PermalinkResolutionService(
        PermalinkManager manager,
        MediaPermalinkLeaseRepository leases,
        ILibraryManager library,
        IUserManager users,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<PermalinkResolutionService> logger)
    {
        _manager = manager;
        _leases = leases;
        _library = library;
        _users = users;
        _clock = clock;
        _configuration = configuration;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PermalinkCandidateEnvelope>> DiscoverAsync(
        string permalinkId, string purpose, Guid userId, CancellationToken cancellationToken)
    {
        if (purpose is not "details" and not "playback")
        {
            throw Conflict("purpose-invalid", $"Unknown permalink purpose '{purpose}'.");
        }

        await ReclaimAsync(cancellationToken).ConfigureAwait(false);
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
        var secret = MediaPermalinkLeaseRepository.NewSecret();
        document.SecretDigest = MediaPermalinkLeaseRepository.Digest(secret);
        document.Kind = "playback";
        document.ExpiresAtUtc = Now.AddMinutes(5);
        document.UpdatedAtUtc = Now;
        await _leases.SaveAsync(document, null, cancellationToken).ConfigureAwait(false);
        return Envelope(new IssuedLease(document, secret), 0);
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
        return await RedeemPlaybackCoreAsync(
            handle, lease, playbackSessionId, queueOrdinal, complete, userId, cancellationToken, readyV1: true).ConfigureAwait(false);
    }

    public async Task<PermalinkPlaybackSnapshot> RedeemPlaybackAsync(
        string handle,
        string lease,
        string playbackSessionId,
        int queueOrdinal,
        bool complete,
        Guid userId,
        CancellationToken cancellationToken,
        bool clientHeldLease = true)
    {
        return await RedeemPlaybackCoreAsync(
            handle, lease, playbackSessionId, queueOrdinal, complete, userId, cancellationToken, readyV1: false).ConfigureAwait(false);
    }

    /// <summary>Consumes or continues a user-bound logical playback session.</summary>
    /// <param name="handle">The candidate handle.</param>
    /// <param name="lease">The candidate secret.</param>
    /// <param name="playbackSessionId">The playback session identifier.</param>
    /// <param name="queueOrdinal">The queue entry to consume.</param>
    /// <param name="complete">Whether this completes the session.</param>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="readyV1">Whether the details credential owns the session.</param>
    /// <returns>The current playback entry.</returns>
    private async Task<PermalinkPlaybackSnapshot> RedeemPlaybackCoreAsync(
        string handle,
        string lease,
        string playbackSessionId,
        int queueOrdinal,
        bool complete,
        Guid userId,
        CancellationToken cancellationToken,
        bool readyV1)
    {
        if (string.IsNullOrWhiteSpace(playbackSessionId) || playbackSessionId.Length > 128)
        {
            throw Conflict("playback-session-required", "A playback session of at most 128 characters is required.");
        }

        var document = await ReadLeaseAsync(new LeaseCredentials(handle, lease, userId, readyV1 ? "details" : "playback"), cancellationToken).ConfigureAwait(false);
        var candidate = await ResolveAsync(document, cancellationToken).ConfigureAwait(false);
        PlaybackQueue plan;
        if (document.ConsumedAtUtc is null)
        {
            if (queueOrdinal != 0)
            {
                throw Conflict("playback-ordinal-skipped", "The first playback redemption must consume queue ordinal 0.");
            }

            plan = await BuildPlanAsync(Representative(candidate), userId, cancellationToken).ConfigureAwait(false);
            plan = plan with { InstanceId = _instanceId, LastOrdinal = 0, ReadyV1 = readyV1 };
            document.PlaybackSessionId = playbackSessionId;
            document.ConsumedAtUtc = Now;
        }
        else
        {
            if (!string.Equals(document.PlaybackSessionId, playbackSessionId, StringComparison.Ordinal))
            {
                throw Conflict("playback-lease-consumed", "The playback lease was consumed by another session.");
            }

            plan = ReadPlan(document);
            if (!plan.InstanceId.Equals(_instanceId) || plan.ReadyV1 != readyV1)
            {
                throw Conflict("playback-lease-consumed", "The playback lease belongs to a previous session or server instance.");
            }

            if (queueOrdinal < plan.LastOrdinal || queueOrdinal > plan.LastOrdinal + 1)
            {
                throw Conflict("playback-ordinal-skipped", "Playback must advance through the queue in order.");
            }
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
        var version = visible.SingleOrDefault(value => value.ContentId.Equals(entry.ContentId))
            ?? throw Conflict("playback-item-unavailable", "This queued title is no longer available to the current user.");
        var item = version.Items.FirstOrDefault(value => value.Id.Equals(entry.ItemId)) ?? Representative(version);
        var paths = item is Video { VideoType: VideoType.Dvd or VideoType.BluRay }
            ? OpticalPaths(item.Path)
            : item is Video video
                ? new[] { item.Path }.Concat(video.AdditionalParts).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        if (paths.Length == 0)
        {
            throw Conflict("playback-source-missing", $"Library item '{item.Id}' has no current media source.");
        }

        plan = plan with { LastOrdinal = queueOrdinal };
        document.PlaybackPlan = JsonSerializer.Serialize(plan);
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

    /// <summary>Lists media members in the library item's optical media tree.</summary>
    /// <param name="root">The library item's optical directory.</param>
    /// <returns>The ordered media member paths.</returns>
    private static string[] OpticalPaths(string root)
    {
        var bluray = Path.Combine(root, "BDMV");
        var dvd = Path.Combine(root, "VIDEO_TS");
        var tree = Directory.Exists(bluray) ? bluray : dvd;
        if (!Directory.Exists(tree))
        {
            throw Conflict("playback-source-missing", "The optical media directory is unavailable.");
        }

        return Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reclaims a bounded page of inactive logical lease state.</summary>
    /// <param name="token">The cancellation token.</param>
    /// <returns>A task representing the reclamation pass.</returns>
    private async Task ReclaimAsync(CancellationToken token)
    {
        if (!_configuration.GetValue(PermalinkPlaybackRecovery.EnabledKey, false))
        {
            return;
        }

        var retention = _configuration.GetValue(PermalinkPlaybackRecovery.TerminalRetentionKey, TimeSpan.FromHours(24));
        var maximum = _configuration.GetValue(PermalinkPlaybackRecovery.MaximumEntriesPerPassKey, 32);
        maximum = Math.Clamp(maximum, 1, 1024);
        var offset = (Interlocked.Add(ref _reclamationOffset, maximum) - maximum) & int.MaxValue;
        var candidates = await _leases.ReclamationCandidatesAsync(Now, offset, maximum, token).ConfigureAwait(false);
        if (candidates.Count < maximum)
        {
            Interlocked.Exchange(ref _reclamationOffset, 0);
        }

        foreach (var lease in candidates)
        {
            var reclaim = lease.ExpiresAtUtc <= Now
                && (lease.ConsumedAtUtc is null || (lease.Kind == "details" && lease.PlaybackPlan is null));
            if (lease.ConsumedAtUtc.HasValue && lease.PlaybackPlan is not null)
            {
                try
                {
                    var plan = ReadPlan(lease);
                    reclaim = !plan.InstanceId.Equals(_instanceId) || (lease.CompletedAtUtc.HasValue && lease.CompletedAtUtc.Value + retention <= Now);
                }
                catch (PermalinkException exception)
                {
                    _logger.LogWarning(exception, "Cannot reclaim corrupt permalink lease for content {ContentId} and purpose {Purpose}", lease.ContentId, lease.Kind);
                }
            }

            if (reclaim)
            {
                await _leases.DeleteIfUnchangedAsync(lease, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<MediaPermalinkLease> ReadLeaseAsync(LeaseCredentials credentials, CancellationToken token)
    {
        if (!IsToken(credentials.Handle) || !IsToken(credentials.Secret))
        {
            throw Conflict("lease-invalid", "The permalink lease is invalid.");
        }

        var document = await _leases.ReadAsync(credentials.Handle, token).ConfigureAwait(false);
        if (document is null || !document.UserId.Equals(credentials.UserId) || document.Kind != credentials.Purpose
            || document.DeletedAtUtc.HasValue || (document.ConsumedAtUtc is null && document.ExpiresAtUtc <= Now)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(document.SecretDigest),
                Encoding.UTF8.GetBytes(MediaPermalinkLeaseRepository.Digest(credentials.Secret))))
        {
            throw Conflict("lease-invalid", "The permalink lease is invalid or has expired.");
        }

        var user = _users.GetUserById(credentials.UserId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            throw Conflict("lease-invalid", "The permalink user is unavailable or disabled.");
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
        return candidates.SingleOrDefault(value => value.ContentId.Equals(lease.ContentId))
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

    private static BaseItem Representative(PermalinkManager.Candidate candidate) => candidate.Items
        .OrderBy(item => item is Video { PrimaryVersionId: not null } ? 1 : 0).ThenBy(item => item.Id).First();

    private static PermalinkCandidateEnvelope Envelope(IssuedLease issued, int rank) => new(
        rank,
        issued.Document.Alias.StartsWith("sk-", StringComparison.Ordinal) ? "sloptank" : "external",
        issued.Document.Handle,
        issued.Secret);

    private static bool IsToken(string value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept(_tokenCharacters) < 0;

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
            return plan is { Version: 1, Entries.Count: > 0 and <= 100 }
                && plan.LastOrdinal >= 0 && plan.LastOrdinal < plan.Entries.Count
                && plan.Entries.All(entry => !entry.ContentId.Equals(Guid.Empty) && !entry.ItemId.Equals(Guid.Empty))
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

    private sealed record PlaybackQueue(int Version, IReadOnlyList<QueueEntry> Entries, Guid InstanceId = default, int LastOrdinal = 0, bool ReadyV1 = false);
}
