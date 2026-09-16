// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-09-09, 2026-09-16.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns private, user-bound, single-use permalink lease publication.
/// </summary>
internal sealed class PermalinkLeaseStore
{
    private readonly ConcurrentDictionary<string, PermalinkLeaseDocument> _leases = new();
    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    public PermalinkLeaseStore(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
    }

    public async Task<PermalinkCandidateEnvelope> IssueAsync(
        PermalinkResolutionBinding binding,
        LeaseEvidence evidence,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken,
        string? existingHandle = null,
        bool durable = true)
    {
        var handle = existingHandle ?? Token();
        var lease = Token();
        var serverId = await ReadServerIdAsync(cancellationToken).ConfigureAwait(false);
        var document = new PermalinkLeaseDocument(
            handle,
            lease,
            binding.PermalinkId,
            binding.ItemId,
            binding.CapsuleId,
            binding.ContentRoot,
            binding.AnchorToken,
            binding.BindingInstanceId,
            binding.CurrentPath,
            evidence.ObjectIdentity,
            evidence.AssignmentHead,
            evidence.AcceptedProviderDigest,
            evidence.ActiveAliasSetDigest,
            purpose,
            userId,
            serverId,
            _timeProvider.GetUtcNow().AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));
        _leases[handle] = document;
        var root = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-resolution-leases",
            handle);
        _fileSystem.CreateDirectoryDurable(root);
        var leasePath = Path.Combine(root, existingHandle is null ? "lease.json" : "playback-lease.json");
        if (durable)
        {
            await _fileSystem.PublishImmutableAsync(
                leasePath,
                CanonicalJson.Serialize(document),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _fileSystem.PublishUnflushedAsync(
                leasePath,
                CanonicalJson.Serialize(document),
                cancellationToken).ConfigureAwait(false);
        }

        return new PermalinkCandidateEnvelope(0, Namespace(binding.PermalinkId), handle, lease);
    }

    public async Task<PermalinkLeaseDocument> ConsumeAsync(
        string handle,
        string lease,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var document = await ValidateAsync(
            handle,
            lease,
            purpose,
            userId,
            allowConsumed: false,
            cancellationToken).ConfigureAwait(false);
        await PublishConsumptionAsync(
            document,
            idempotent: false,
            cancellationToken).ConfigureAwait(false);
        return document;
    }

    public Task MarkPlaybackConsumedAsync(
        PermalinkLeaseDocument document,
        CancellationToken cancellationToken)
    {
        return PublishConsumptionAsync(
            document,
            idempotent: true,
            cancellationToken);
    }

    private async Task PublishConsumptionAsync(
        PermalinkLeaseDocument document,
        bool idempotent,
        CancellationToken cancellationToken)
    {
        var handle = document.Handle;
        var purpose = document.Purpose;
        var userId = document.UserId;
        var root = ResolutionRoot(handle);
        var consumedPath = Path.Combine(
            root,
            "consumed-" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(document.Lease))) + ".json");
        try
        {
            await _fileSystem.PublishImmutableAsync(
                consumedPath,
                CanonicalJson.Serialize(new PermalinkLeaseConsumption(
                    handle,
                    purpose,
                    userId,
                    document.ServerId,
                    _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
        {
            if (idempotent)
            {
                var existing = CanonicalJson.Deserialize<PermalinkLeaseConsumption>(
                    await File.ReadAllBytesAsync(consumedPath, cancellationToken).ConfigureAwait(false),
                    consumedPath);
                if (string.Equals(existing.Handle, handle, StringComparison.Ordinal)
                    && string.Equals(existing.Purpose, purpose, StringComparison.Ordinal)
                    && existing.UserId.Equals(userId)
                    && existing.ServerId.Equals(document.ServerId))
                {
                    return;
                }
            }

            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "lease-consumed",
                "The permalink lease has already been consumed.",
                exception);
        }

        _ = _leases.TryRemove(handle, out _);
    }

    public Task<PermalinkLeaseDocument> ValidatePlaybackAsync(
        string handle,
        string lease,
        Guid userId,
        bool allowConsumed,
        CancellationToken cancellationToken)
    {
        return ValidateAsync(
            handle,
            lease,
            "playback",
            userId,
            allowConsumed,
            cancellationToken);
    }

    private async Task<PermalinkLeaseDocument> ValidateAsync(
        string handle,
        string lease,
        string purpose,
        Guid userId,
        bool allowConsumed,
        CancellationToken cancellationToken)
    {
        var document = await ReadLeaseAsync(handle, lease, purpose, userId, cancellationToken)
            .ConfigureAwait(false);
        var serverId = await ReadServerIdAsync(cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(document.Lease),
                Encoding.UTF8.GetBytes(lease))
            || !document.UserId.Equals(userId)
            || !document.ServerId.Equals(serverId)
            || !string.Equals(document.Purpose, purpose, StringComparison.Ordinal)
            || DateTimeOffset.Parse(document.ExpiresAt, CultureInfo.InvariantCulture)
                <= _timeProvider.GetUtcNow())
        {
            throw Invalid();
        }

        var consumedPath = Path.Combine(
            ResolutionRoot(handle),
            "consumed-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lease))) + ".json");
        if (!allowConsumed && File.Exists(consumedPath))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "lease-consumed",
                "The permalink lease has already been consumed.");
        }

        return document;
    }

    private async Task<PermalinkLeaseDocument> ReadLeaseAsync(
        string handle,
        string lease,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (_leases.TryGetValue(handle, out var cached)
            && string.Equals(cached.Lease, lease, StringComparison.Ordinal))
        {
            return cached;
        }

        foreach (var fileName in new[] { "playback-lease.json", "lease.json" })
        {
            var path = Path.Combine(ResolutionRoot(handle), fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            PermalinkLeaseDocument document;
            try
            {
                document = CanonicalJson.Deserialize<PermalinkLeaseDocument>(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                    path);
            }
            catch (PermalinkException)
            {
                // Torn content from an unflushed server-confined lease lost to a crash
                // reads as missing; the consumption marker below still decides the verdict.
                continue;
            }

            if (string.Equals(document.Lease, lease, StringComparison.Ordinal))
            {
                _leases[handle] = document;
                return document;
            }
        }

        throw await ResolveMissingLeaseAsync(handle, lease, purpose, userId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PermalinkException> ResolveMissingLeaseAsync(
        string handle,
        string lease,
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var consumedPath = Path.Combine(
            ResolutionRoot(handle),
            "consumed-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lease))) + ".json");
        if (!File.Exists(consumedPath))
        {
            return Invalid();
        }

        PermalinkLeaseConsumption existing;
        try
        {
            existing = CanonicalJson.Deserialize<PermalinkLeaseConsumption>(
                await File.ReadAllBytesAsync(consumedPath, cancellationToken).ConfigureAwait(false),
                consumedPath);
        }
        catch (PermalinkException)
        {
            return Invalid();
        }

        var serverId = await ReadServerIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(existing.Handle, handle, StringComparison.Ordinal)
            && string.Equals(existing.Purpose, purpose, StringComparison.Ordinal)
            && existing.UserId.Equals(userId)
            && existing.ServerId.Equals(serverId))
        {
            return new PermalinkException(
                PermalinkErrorKind.Conflict,
                "lease-consumed",
                "The permalink lease has already been consumed.");
        }

        // Accepted corner: an expired consumed lease whose unflushed document was lost
        // reports consumed here instead of invalid (the marker carries no expiry). Both
        // are 409 re-mint signals and the web client maps every 409 to one state.
        return Invalid();
    }

    private async Task<Guid> ReadServerIdAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalinks",
            "authority.json");
        var document = CanonicalJson.Deserialize<PermalinkAuthorityDocument>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
        return document.AuthorityId;
    }

    private string ResolutionRoot(string handle)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-resolution-leases",
            handle);
    }

    private static PermalinkException Invalid()
    {
        return new PermalinkException(
            PermalinkErrorKind.Conflict,
            "lease-invalid",
            "The permalink lease is expired, consumed, user-mismatched, server-mismatched, or purpose-mismatched.");
    }

    private static string Token()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static string Namespace(string permalinkId)
    {
        return permalinkId.StartsWith("sk-", StringComparison.Ordinal) ? "sloptank" : "external";
    }

    internal sealed record LeaseEvidence(
        string ObjectIdentity,
        string AssignmentHead,
        string AcceptedProviderDigest,
        string ActiveAliasSetDigest);

    private sealed record PermalinkLeaseConsumption(
        string Handle,
        string Purpose,
        Guid UserId,
        Guid ServerId,
        string ConsumedAt);
}
