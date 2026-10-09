// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.

using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace SlopTank.ExternalAuth;

/// <summary>Options of the external-session mint store.</summary>
public sealed record ExternalAuthSessionMintOptions
{
    /// <summary>Hard bound on live handles for one user. Deterministic
    /// refusal at the bound; never eviction of a live session.</summary>
    public int MaxLiveSessionsPerUser { get; init; } = 8;

    /// <summary>Hard server-wide bound on live handles: TTL alone is not a
    /// memory bound.</summary>
    public int MaxLiveSessionsTotal { get; init; } = 10_000;

    /// <summary>Lifetime of one minted handle. The handle is swept after
    /// expiry; the underlying server session follows the server's own token
    /// lifetime.</summary>
    public TimeSpan SessionTimeToLive { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The session cookie name carrying only the opaque
    /// handle.</summary>
    public string SessionCookieName { get; init; } = "sloptank-extauth-session";

    /// <summary>Interval of the open-channel revalidation pass: every
    /// tracked channel's handle is re-resolved (aborting dead ones) and the
    /// store's expired handles are swept, so expiry, logout and user
    /// disable close open channels and dead entries can never accumulate.
    /// Tests tighten it to drive the real timer; production keeps the
    /// default.</summary>
    public TimeSpan RevalidationInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Typed invalid options at the external-session security boundary.</summary>
public enum ExternalAuthMintOptionsErrorCode
{
    /// <summary>The open-channel sweep interval is outside the enforced range.</summary>
    RevalidationIntervalOutOfRange = 1,
}

/// <summary>A typed refusal of unsafe external-session options.</summary>
public sealed class ExternalAuthMintOptionsException : ArgumentOutOfRangeException
{
    /// <summary>Initializes the typed interval refusal.</summary>
    public ExternalAuthMintOptionsException()
        : base(nameof(ExternalAuthSessionMintOptions.RevalidationInterval),
            "Open-channel revalidation interval must be between 10 milliseconds and 5 minutes")
    {
    }

    /// <summary>Gets the machine-readable refusal code.</summary>
    public ExternalAuthMintOptionsErrorCode Code => ExternalAuthMintOptionsErrorCode.RevalidationIntervalOutOfRange;
}

/// <summary>
/// One minted external session: the cookie carries only the opaque
/// <paramref name="Handle"/>; the access token stays server-side in this
/// record and is never returned to any client or written to any log.
/// </summary>
/// <param name="Handle">Opaque base64url handle (the cookie value).</param>
/// <param name="AccessToken">The server-minted access token; server-side
/// only.</param>
/// <param name="UserId">The linked local user.</param>
/// <param name="DeviceId">The session's device id.</param>
/// <param name="AppName">The client application name.</param>
/// <param name="ProviderId">The immutable provider instance the mint is
/// bound to.</param>
/// <param name="ProviderEpoch">The provider configuration epoch the mint is
/// bound to.</param>
/// <param name="CreatedAt">Mint time.</param>
/// <param name="ExpiresAt">Handle expiry.</param>
public sealed record ExternalAuthMintedSession(
    [property: JsonIgnore] string Handle,
    [property: JsonIgnore] string AccessToken,
    Guid UserId,
    string DeviceId,
    string AppName,
    string ProviderId,
    long ProviderEpoch,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Renders session context without a cookie handle or access token.</summary>
    public override string ToString() =>
        $"{nameof(ExternalAuthMintedSession)} {{ UserId = {UserId}, ProviderId = {ProviderId}, ProviderEpoch = {ProviderEpoch}, Handle = [REDACTED], AccessToken = [REDACTED] }}";
}

/// <summary>Typed outcome of minting an external session.</summary>
public enum ExternalAuthMintStatus
{
    /// <summary>The session was minted and its handle cookie set.</summary>
    Minted = 0,

    /// <summary>A hard live-handle cap (per-user or server-wide) was
    /// reached; nothing was minted.</summary>
    CapReached = 1,

    /// <summary>The transport may not carry the session cookie (not HTTPS,
    /// and the development HTTP profile is not explicitly active).</summary>
    TransportInsecure = 2,

    /// <summary>The host could not mint the underlying server
    /// session.</summary>
    MintRefused = 3,
}

/// <summary>Result of a mint attempt.</summary>
/// <param name="Status">The typed status.</param>
/// <param name="Session">The minted session on success.</param>
public sealed record ExternalAuthMintResult(ExternalAuthMintStatus Status, ExternalAuthMintedSession? Session)
{
    /// <summary>Gets a value indicating whether a session was minted.</summary>
    public bool IsMinted => Status == ExternalAuthMintStatus.Minted && Session is not null;
}

/// <summary>The identity-independent mint request a provider's completed
/// login flow submits.</summary>
/// <param name="UserId">The linked local user the session is minted
/// for.</param>
/// <param name="DeviceId">The client device id of the minted session.</param>
/// <param name="AppName">The client application name.</param>
/// <param name="AppVersion">The client application version.</param>
/// <param name="DeviceName">The client device name.</param>
/// <param name="ProviderId">The immutable provider instance id the flow
/// completed under.</param>
/// <param name="ProviderEpoch">The provider configuration epoch the flow
/// completed under.</param>
public sealed record ExternalAuthMintRequest(
    Guid UserId,
    string DeviceId,
    string AppName,
    string AppVersion,
    string DeviceName,
    string ProviderId,
    long ProviderEpoch);

/// <summary>
/// The host-provided session minter: mints a REAL server session for a
/// linked local user through the server's own session manager, retains the
/// access token server-side, and answers with an opaque cookie handle. The
/// provider plugin never sees or stores the token.
/// </summary>
public interface IExternalAuthSessionMinter
{
    /// <summary>
    /// Mints one external session and sets its opaque-handle cookie on the
    /// response. Refuses typed on an insecure transport (production mints
    /// require actual HTTPS) and at the hard live-handle caps, cleaning up
    /// the underlying session when the store refused the handle.
    /// </summary>
    /// <param name="request">The mint request.</param>
    /// <param name="httpContext">The callback response carrying the
    /// browser.</param>
    /// <param name="cancellationToken">Cancels the mint.</param>
    /// <returns>The typed mint result; the outcome document must not claim
    /// a session unless this is minted.</returns>
    ValueTask<ExternalAuthMintResult> MintAsync(
        ExternalAuthMintRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// The generic, host-agnostic mint store of cookie-carried external
/// sessions: bounded live-handle cardinality (per user and server-wide,
/// deterministic refusal at the bound), snapshot-safe expiry sweeps that can
/// never remove a session created during the sweep, typed provider-scoped
/// revocation, and a revocation event an open-channel tracker can close
/// connections from. The access tokens stay server-side; the cookie carries
/// only the opaque handle.
/// </summary>
public sealed class ExternalAuthSessionMintStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExternalAuthMintedSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly ExternalAuthSessionMintOptions _options;

    /// <summary>
    /// Fires when a live session is revoked (explicitly or by provider
    /// scope); carries the revoked record so open cookie-authenticated
    /// channels can be closed immediately.
    /// </summary>
    public event Action<ExternalAuthMintedSession>? SessionRevoked;

    /// <summary>Initializes the store.</summary>
    /// <param name="options">The store options.</param>
    /// <param name="clock">Optional time source.</param>
    public ExternalAuthSessionMintStore(ExternalAuthSessionMintOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new ExternalAuthSessionMintOptions();
        if (_options.RevalidationInterval < TimeSpan.FromMilliseconds(10)
            || _options.RevalidationInterval > TimeSpan.FromMinutes(5))
        {
            throw new ExternalAuthMintOptionsException();
        }

        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the configured options.</summary>
    public ExternalAuthSessionMintOptions Options => _options;

    /// <summary>
    /// Optional host-supplied liveness probe: a live-by-TTL handle whose
    /// underlying server session is gone (logout, user disable, token
    /// replacement) resolves as dead. The host sets this once at startup;
    /// it must be cheap, synchronous and side-effect free.
    /// </summary>
    public Func<ExternalAuthMintedSession, bool>? SessionLivenessProbe { get; set; }

    /// <summary>Gets the number of live handles (expired-but-unswept
    /// included).</summary>
    public int LiveHandleCount
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Inserts a minted session. Caps are enforced deterministically: at a
    /// bound the insert is refused, never an eviction of a live session.
    /// </summary>
    /// <param name="session">The session record.</param>
    /// <returns>True when inserted; false when a cap was reached.</returns>
    public bool Insert(ExternalAuthMintedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            // Count only live sessions against the caps.
            var liveForUser = 0;
            foreach (var existing in _sessions.Values)
            {
                if (existing.ExpiresAt > now)
                {
                    liveForUser += existing.UserId.Equals(session.UserId) ? 1 : 0;
                }
            }

            if (liveForUser >= _options.MaxLiveSessionsPerUser)
            {
                return false;
            }

            var liveTotal = _sessions.Values.Count(s => s.ExpiresAt > now);
            if (liveTotal >= _options.MaxLiveSessionsTotal)
            {
                return false;
            }

            _sessions[session.Handle] = session;
            return true;
        }
    }

    /// <summary>
    /// Resolves one handle to its live session; expired or revoked handles
    /// answer null.
    /// </summary>
    /// <param name="handle">The cookie handle.</param>
    /// <returns>The live session, or null.</returns>
    public ExternalAuthMintedSession? Resolve(string handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return null;
        }

        lock (_sync)
        {
            return _sessions.TryGetValue(handle, out var session)
                && session.ExpiresAt > _clock.GetUtcNow()
                && (SessionLivenessProbe is null || SessionLivenessProbe(session))
                ? session
                : null;
        }
    }

    /// <summary>Revokes exactly one handle; a no-op when it is not
    /// live.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>True when a live session was revoked.</returns>
    public bool RevokeHandle(string handle)
    {
        ExternalAuthMintedSession? revoked = null;
        lock (_sync)
        {
            if (_sessions.Remove(handle, out var session))
            {
                revoked = session;
            }
        }

        if (revoked is not null)
        {
            SessionRevoked?.Invoke(revoked);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Revokes every live session bound to one provider instance (any epoch
    /// of that id): disable, removal or incompatible downgrade removes
    /// exactly the affected sessions, without remote IdP availability.
    /// </summary>
    /// <param name="providerId">The provider instance id.</param>
    /// <returns>The number of revoked sessions.</returns>
    public int RevokeByProvider(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return 0;
        }

        List<ExternalAuthMintedSession> revoked = [];
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var (handle, session) in _sessions)
            {
                if (session.ProviderId == providerId && session.ExpiresAt > now)
                {
                    revoked.Add(session);
                }
            }

            foreach (var session in revoked)
            {
                _sessions.Remove(session.Handle);
            }
        }

        foreach (var session in revoked)
        {
            SessionRevoked?.Invoke(session);
        }

        return revoked.Count;
    }

    /// <summary>
    /// Sweeps expired handles. Snapshot-safe by construction: the removal
    /// candidates are fixed before any removal, so a session created during
    /// the sweep can never be removed by it.
    /// </summary>
    /// <returns>The number of swept handles.</returns>
    public int SweepExpired()
    {
        List<string> expired;
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            expired = _sessions
                .Where(kv => kv.Value.ExpiresAt <= now)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var handle in expired)
            {
                _sessions.Remove(handle);
            }
        }

        return expired.Count;
    }

    /// <summary>Generates a fresh opaque handle.</summary>
    /// <returns>The base64url handle.</returns>
    public static string NewHandle() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
