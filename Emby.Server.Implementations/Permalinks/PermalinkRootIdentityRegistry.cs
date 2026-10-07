// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-03, 2026-10-07.
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Decides whether a configured root is KNOWN (marker, registry row, authority and device:inode all
/// agree), registers a brand-new root, or refuses an UNKNOWN one (P6 decisions, "Root identity
/// registration"). An UNKNOWN root is never auto re-registered: a marker without a row, a row
/// without a marker, a marker from another authority and a device:inode mismatch all need an
/// operator, because re-registering would silently mint a second identity for the same files.
/// </summary>
internal sealed class PermalinkRootIdentityRegistry : IDisposable
{
    /// <summary>The error code reported for an UNKNOWN root.</summary>
    public const string UnknownCode = "permalink-root-identity-unknown";

    private const int SqliteUniqueConstraintExtendedCode = 2067;
    private readonly PermalinkRootIdentityStore _store;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly IPermalinkPathObjects _objects;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private readonly ConcurrentDictionary<AuthorityRootKey, KnownRoot> _known = new();
    private readonly ConcurrentDictionary<PermalinkAuthorityNamespace, Guid> _authorityIds = new();

    public PermalinkRootIdentityRegistry(
        PermalinkRootIdentityStore store,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects,
        TimeProvider timeProvider)
    {
        _store = store;
        _fileSystem = fileSystem;
        _objects = objects;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the identity of a configured root, registering it when it is brand new.
    /// </summary>
    /// <param name="configuredRoot">The configured content-root or evidence-root path (an alias is fine).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The KNOWN root identity.</returns>
    /// <exception cref="PermalinkException">Unavailable with code <see cref="UnknownCode"/> for an UNKNOWN root.</exception>
    public async Task<PermalinkRootIdentity> EnsureKnownAsync(string configuredRoot, CancellationToken cancellationToken)
    {
        var canonical = _objects.CanonicalizeDirectory(configuredRoot)
            ?? throw Unknown(configuredRoot, "the configured root does not exist or cannot be resolved");
        var markerPath = Path.Combine(canonical, PermalinkRootMarker.FileName);
        var authorityNamespace = _store.AuthorityNamespace;
        var cacheKey = new AuthorityRootKey(authorityNamespace, canonical);
        var rootPin = _objects.ReadPinnedIdentityNoFollow(canonical);
        var markerPin = _objects.ReadPinnedIdentityNoFollow(markerPath);
        if (_known.TryGetValue(cacheKey, out var cached)
            && rootPin is not null
            && markerPin is not null
            && string.Equals(cached.RootPin, rootPin, StringComparison.Ordinal)
            && string.Equals(cached.MarkerPin, markerPin, StringComparison.Ordinal)
            && authorityNamespace == _store.AuthorityNamespace)
        {
            return cached.Identity;
        }

        await _registrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAuthorityNamespace(authorityNamespace, canonical);
            return await ResolveLockedAsync(authorityNamespace, canonical, markerPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _registrationLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _registrationLock.Dispose();

    private static (string Digest, string Pin) SplitAttestation(string attestation)
    {
        var split = attestation.LastIndexOf('|');
        return split < 0 ? (attestation, string.Empty) : (attestation[..split], attestation[(split + 1)..]);
    }

    private static PermalinkException Unknown(string root, string reason, Exception? cause = null)
        => new(
            PermalinkErrorKind.Unavailable,
            UnknownCode,
            $"The configured root '{root}' has no verifiable permalink identity ({reason}). It is not re-registered automatically.",
            cause);

    private async Task<PermalinkRootIdentity> ResolveLockedAsync(
        PermalinkAuthorityNamespace authorityNamespace,
        string canonical,
        string markerPath,
        CancellationToken cancellationToken)
    {
        var rootPin = _objects.ReadPinnedIdentityNoFollow(canonical)
            ?? throw Unknown(canonical, "the configured root cannot be inspected");
        var markerBytes = _objects.ReadFileIfExists(markerPath);
        var rowForRoot = await _store.FindByRootAsync(canonical, authorityNamespace, cancellationToken).ConfigureAwait(false);
        RequireAuthorityNamespace(authorityNamespace, canonical);
        if (markerBytes is null)
        {
            if (rowForRoot is not null)
            {
                throw Unknown(canonical, "the registry has this root but its marker file is missing");
            }

            return await RegisterAsync(authorityNamespace, canonical, markerPath, rootPin, cancellationToken).ConfigureAwait(false);
        }

        var marker = CanonicalJson.Deserialize<PermalinkRootMarker>(markerBytes, markerPath);
        var authorityId = await ReadAuthorityIdAsync(authorityNamespace, canonical, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(marker.AuthorityId.ToString("D"), authorityId.ToString("D"), StringComparison.Ordinal))
        {
            throw Unknown(canonical, "the marker belongs to a different authority");
        }

        var markerId = marker.RootIdentityId.ToString("D");
        var row = await _store.FindByIdAsync(markerId, authorityNamespace, cancellationToken).ConfigureAwait(false)
            ?? throw Unknown(canonical, "the marker names a root identity the registry does not have");
        RequireAuthorityNamespace(authorityNamespace, canonical);
        var digest = CanonicalJson.Digest(markerBytes);
        var (expectedDigest, expectedPin) = SplitAttestation(row.Attestation);
        if (marker.Generation != row.Generation
            || !string.Equals(expectedDigest, digest, StringComparison.Ordinal))
        {
            throw Unknown(canonical, "the marker does not match the registered attestation");
        }

        if (!string.Equals(expectedPin, rootPin, StringComparison.Ordinal))
        {
            throw Unknown(
                canonical,
                $"the root device:inode {rootPin} does not match the attested {expectedPin}");
        }

        var identity = new PermalinkRootIdentity(row.RootIdentityId, row.Generation, canonical);
        RequireAuthorityNamespace(authorityNamespace, canonical);
        Remember(authorityNamespace, canonical, markerPath, rootPin, identity);
        RequireAuthorityNamespace(authorityNamespace, canonical);
        return identity;
    }

    private async Task<PermalinkRootIdentity> RegisterAsync(
        PermalinkAuthorityNamespace authorityNamespace,
        string canonical,
        string markerPath,
        string rootPin,
        CancellationToken cancellationToken)
    {
        var authorityId = await ReadAuthorityIdAsync(authorityNamespace, canonical, cancellationToken).ConfigureAwait(false);
        var identityId = Guid.NewGuid();
        var marker = new PermalinkRootMarker(
            authorityId,
            identityId,
            1,
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        var bytes = CanonicalJson.Serialize(marker);
        try
        {
            RequireAuthorityNamespace(authorityNamespace, canonical);
            await _fileSystem.PublishImmutableAsync(markerPath, bytes, cancellationToken).ConfigureAwait(false);
            RequireAuthorityNamespace(authorityNamespace, canonical);
        }
        catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
        {
            throw Unknown(canonical, "a marker appeared while registering this root", exception);
        }
        catch
        {
            RemoveOwnMarker(markerPath, bytes);
            throw;
        }

        var row = new PermalinkRootIdentityRow(
            identityId.ToString("D"),
            1,
            canonical,
            CanonicalJson.Digest(bytes) + "|" + rootPin);
        try
        {
            await _store.InsertAsync(row, authorityNamespace, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteUniqueConstraintExtendedCode)
        {
            RemoveOwnMarker(markerPath, bytes);
            throw Unknown(canonical, "the registry already holds this root", exception);
        }
        catch
        {
            RemoveOwnMarker(markerPath, bytes);
            throw;
        }

        // The scoped store guarantees the row went to the expected database. If ResetTo occurred
        // after that write, leave its matching marker and row intact in the old authority and
        // refuse to return either identity in the newly selected authority.
        RequireAuthorityNamespace(authorityNamespace, canonical);
        var identity = new PermalinkRootIdentity(row.RootIdentityId, 1, canonical);
        Remember(authorityNamespace, canonical, markerPath, rootPin, identity);
        RequireAuthorityNamespace(authorityNamespace, canonical);
        return identity;
    }

    private async Task<Guid> ReadAuthorityIdAsync(
        PermalinkAuthorityNamespace authorityNamespace,
        string canonical,
        CancellationToken cancellationToken)
    {
        if (_authorityIds.TryGetValue(authorityNamespace, out var cached))
        {
            RequireAuthorityNamespace(authorityNamespace, canonical);
            return cached;
        }

        var authorityId = await _store.ReadAuthorityIdAsync(authorityNamespace, cancellationToken).ConfigureAwait(false);
        RequireAuthorityNamespace(authorityNamespace, canonical);

        return _authorityIds.GetOrAdd(authorityNamespace, authorityId);
    }

    private void RemoveOwnMarker(string markerPath, ReadOnlyMemory<byte> written)
    {
        // The row insert failed after the marker was created: take the marker back so the next
        // attempt starts from "no marker, no row" instead of an UNKNOWN root. Only remove it
        // when it is still exactly the bytes this call wrote.
        var current = _objects.ReadFileIfExists(markerPath);
        if (current is not null && current.AsSpan().SequenceEqual(written.Span))
        {
            File.Delete(markerPath);
        }
    }

    private void Remember(
        PermalinkAuthorityNamespace authorityNamespace,
        string canonical,
        string markerPath,
        string rootPin,
        PermalinkRootIdentity identity)
    {
        var markerPin = _objects.ReadPinnedIdentityNoFollow(markerPath);
        if (markerPin is not null)
        {
            _known[new AuthorityRootKey(authorityNamespace, canonical)] = new KnownRoot(identity, rootPin, markerPin);
        }
    }

    private void RequireAuthorityNamespace(PermalinkAuthorityNamespace expected, string canonical)
    {
        if (expected != _store.AuthorityNamespace)
        {
            throw Unknown(canonical, "the authority changed while the root identity was being checked");
        }
    }

    private readonly record struct AuthorityRootKey(
        PermalinkAuthorityNamespace AuthorityNamespace,
        string CanonicalRoot);

    private sealed record KnownRoot(PermalinkRootIdentity Identity, string RootPin, string MarkerPin);
}
