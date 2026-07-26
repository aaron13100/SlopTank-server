using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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
        string purpose,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var handle = Token();
        var lease = Token();
        var document = new PermalinkLeaseDocument(
            handle,
            lease,
            binding.PermalinkId,
            binding.ItemId,
            binding.CapsuleId,
            binding.ContentRoot,
            binding.AnchorToken,
            binding.CurrentPath,
            purpose,
            userId,
            _timeProvider.GetUtcNow().AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));
        _ = _leases.TryAdd(handle, document);
        var root = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-resolution-leases",
            handle);
        _fileSystem.CreateDirectoryDurable(root);
        await _fileSystem.PublishImmutableAsync(
            Path.Combine(root, "lease.json"),
            CanonicalJson.Serialize(document),
            cancellationToken).ConfigureAwait(false);
        return new PermalinkCandidateEnvelope(0, Namespace(binding.PermalinkId), handle, lease);
    }

    public PermalinkLeaseDocument Consume(string handle, string lease, string purpose, Guid userId)
    {
        if (!_leases.TryRemove(handle, out var document)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(document.Lease),
                System.Text.Encoding.UTF8.GetBytes(lease))
            || document.UserId != userId
            || !string.Equals(document.Purpose, purpose, StringComparison.Ordinal)
            || DateTimeOffset.Parse(document.ExpiresAt, CultureInfo.InvariantCulture)
                <= _timeProvider.GetUtcNow())
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "lease-invalid",
                "The permalink lease is expired, consumed, user-mismatched, or purpose-mismatched.");
        }

        return document;
    }

    private static string Token()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static string Namespace(string permalinkId)
    {
        return permalinkId.StartsWith("sk-", StringComparison.Ordinal) ? "sloptank" : "external";
    }
}

internal sealed record PermalinkLeaseDocument(
    string Handle,
    string Lease,
    string PermalinkId,
    Guid ItemId,
    Guid CapsuleId,
    string ContentRoot,
    string AnchorToken,
    string CurrentPath,
    string Purpose,
    Guid UserId,
    string ExpiresAt);
