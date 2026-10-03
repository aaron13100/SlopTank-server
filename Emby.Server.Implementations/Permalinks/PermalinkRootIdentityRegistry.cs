// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-03.
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
    private readonly ConcurrentDictionary<string, KnownRoot> _known = new(StringComparer.Ordinal);
    private Guid? _authorityId;

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
        var rootPin = _objects.ReadPinnedIdentityNoFollow(canonical);
        var markerPin = _objects.ReadPinnedIdentityNoFollow(markerPath);
        if (_known.TryGetValue(canonical, out var cached)
            && rootPin is not null
            && markerPin is not null
            && string.Equals(cached.RootPin, rootPin, StringComparison.Ordinal)
            && string.Equals(cached.MarkerPin, markerPin, StringComparison.Ordinal))
        {
            return cached.Identity;
        }

        await _registrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveLockedAsync(canonical, markerPath, cancellationToken).ConfigureAwait(false);
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
        string canonical,
        string markerPath,
        CancellationToken cancellationToken)
    {
        var rootPin = _objects.ReadPinnedIdentityNoFollow(canonical)
            ?? throw Unknown(canonical, "the configured root cannot be inspected");
        var markerBytes = _objects.ReadFileIfExists(markerPath);
        var rowForRoot = await _store.FindByRootAsync(canonical, cancellationToken).ConfigureAwait(false);
        if (markerBytes is null)
        {
            if (rowForRoot is not null)
            {
                throw Unknown(canonical, "the registry has this root but its marker file is missing");
            }

            return await RegisterAsync(canonical, markerPath, rootPin, cancellationToken).ConfigureAwait(false);
        }

        var marker = CanonicalJson.Deserialize<PermalinkRootMarker>(markerBytes, markerPath);
        _authorityId ??= await _store.ReadAuthorityIdAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(marker.AuthorityId.ToString("D"), _authorityId.Value.ToString("D"), StringComparison.Ordinal))
        {
            throw Unknown(canonical, "the marker belongs to a different authority");
        }

        var markerId = marker.RootIdentityId.ToString("D");
        var row = await _store.FindByIdAsync(markerId, cancellationToken).ConfigureAwait(false)
            ?? throw Unknown(canonical, "the marker names a root identity the registry does not have");
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
        Remember(canonical, markerPath, rootPin, identity);
        return identity;
    }

    private async Task<PermalinkRootIdentity> RegisterAsync(
        string canonical,
        string markerPath,
        string rootPin,
        CancellationToken cancellationToken)
    {
        _authorityId ??= await _store.ReadAuthorityIdAsync(cancellationToken).ConfigureAwait(false);
        var identityId = Guid.NewGuid();
        var marker = new PermalinkRootMarker(
            _authorityId.Value,
            identityId,
            1,
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        var bytes = CanonicalJson.Serialize(marker);
        try
        {
            await _fileSystem.PublishImmutableAsync(markerPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
        {
            throw Unknown(canonical, "a marker appeared while registering this root", exception);
        }

        var row = new PermalinkRootIdentityRow(
            identityId.ToString("D"),
            1,
            canonical,
            CanonicalJson.Digest(bytes) + "|" + rootPin);
        try
        {
            await _store.InsertAsync(row, cancellationToken).ConfigureAwait(false);
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

        var identity = new PermalinkRootIdentity(row.RootIdentityId, 1, canonical);
        Remember(canonical, markerPath, rootPin, identity);
        return identity;
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

    private void Remember(string canonical, string markerPath, string rootPin, PermalinkRootIdentity identity)
    {
        var markerPin = _objects.ReadPinnedIdentityNoFollow(markerPath);
        if (markerPin is not null)
        {
            _known[canonical] = new KnownRoot(identity, rootPin, markerPin);
        }
    }

    private sealed record KnownRoot(PermalinkRootIdentity Identity, string RootPin, string MarkerPin);
}
