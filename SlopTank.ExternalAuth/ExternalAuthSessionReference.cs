// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.AspNetCore.Http;

namespace SlopTank.ExternalAuth;

/// <summary>
/// How a credential reached the server on the request that captured the
/// session reference. Explicit transports take precedence over cookie
/// transports, and a rejected explicit credential must never fall back to
/// another transport.
/// </summary>
public enum ExternalAuthTransport
{
    /// <summary>No credential was presented.</summary>
    None = 0,

    /// <summary>The standard Authorization header dictionary.</summary>
    AuthorizationHeader = 1,

    /// <summary>A legacy header transport, only when legacy authorization is
    /// enabled server-side.</summary>
    LegacyHeader = 2,

    /// <summary>The ApiKey (or legacy api_key) query parameter.</summary>
    ApiKeyQuery = 3,

    /// <summary>An external cookie session minted by this seam (future
    /// unit).</summary>
    SessionCookie = 4,
}

/// <summary>
/// The origin of the session a reference points at: a local user session
/// (minted by ordinary local authentication), a server API key (no user), or
/// an externally minted session. Link flows may only be initiated from a
/// Local origin; every issued session reference carries this classification
/// before any linking request may rely on it.
/// </summary>
public enum ExternalAuthSessionOrigin
{
    /// <summary>An authenticated local user session token.</summary>
    Local = 0,

    /// <summary>A server API key; not a user identity and never valid for
    /// linking.</summary>
    ApiKey = 1,

    /// <summary>A session minted by the external-login seam itself.</summary>
    External = 2,
}

/// <summary>
/// An opaque server-side handle on the access token backing a captured
/// session. It exists only inside server memory for the lifetime of a
/// pending flow: it is never serialized, logged, or returned to any client,
/// and its string form redacts the value.
/// </summary>
public sealed class ExternalAuthSessionToken
{
    private readonly string _value;

    /// <summary>Initializes the token handle.</summary>
    /// <param name="value">The raw access token; kept server-side
    /// only.</param>
    internal ExternalAuthSessionToken(string value)
    {
        _value = value;
    }

    /// <summary>Creates a token handle from a raw access token.</summary>
    /// <param name="value">The raw token value.</param>
    /// <returns>The opaque handle.</returns>
    public static ExternalAuthSessionToken From(string value) => new(value);

    /// <summary>Gets the raw token value. Server-side callers only: this
    /// property exists so the host session authority can re-resolve the
    /// exact session; it must never be serialized, logged, or returned to
    /// any client.</summary>
    public string Value => _value;

    /// <inheritdoc />
    public override string ToString() => "<session-token-redacted>";
}

/// <summary>
/// A recorded reference to an authenticated session, captured at link-flow
/// initiation and re-resolved at completion. The token fingerprint is a
/// SHA-256 prefix safe for diagnostics; the full token handle is retained
/// server-side only so re-resolution can prove the exact session still
/// exists for the exact user.
/// </summary>
public sealed record ExternalAuthSessionReference(
    Guid UserId,
    string DeviceId,
    string? AppName,
    ExternalAuthTransport Transport,
    ExternalAuthSessionOrigin Origin,
    string TokenFingerprint,
    ExternalAuthSessionToken Token,
    DateTimeOffset CapturedAt);

/// <summary>Typed verdict of re-resolving a recorded session
/// reference.</summary>
public enum ExternalAuthSessionRevalidation
{
    /// <summary>The exact session is still live for the same user and the
    /// user is not disabled.</summary>
    Valid = 0,

    /// <summary>The token no longer resolves (logged out, revoked, or
    /// replaced by a newer login on the same device).</summary>
    Revoked = 1,

    /// <summary>The token resolves to a different user or device than the
    /// reference recorded.</summary>
    Mismatched = 2,

    /// <summary>The user account is disabled.</summary>
    Disabled = 3,

    /// <summary>The reference is of external origin; linking may not rely on
    /// it.</summary>
    ExternalOrigin = 4,
}

/// <summary>Outcome of capturing a session reference from a request.</summary>
public sealed record ExternalAuthSessionCapture
{
    private ExternalAuthSessionCapture(ExternalAuthSessionReference? reference, ExternalAuthErrorCode refusal)
    {
        Reference = reference;
        RefusalCode = refusal;
    }

    /// <summary>Gets the captured reference on success; null
    /// otherwise.</summary>
    public ExternalAuthSessionReference? Reference { get; }

    /// <summary>Gets the typed refusal reason on failure;
    /// <see cref="ExternalAuthErrorCode.None"/> on success.</summary>
    public ExternalAuthErrorCode RefusalCode { get; }

    /// <summary>Gets a value indicating whether a usable local session
    /// reference was captured.</summary>
    public bool IsCaptured => Reference is not null;

    /// <summary>Builds a successful capture.</summary>
    /// <param name="reference">The captured reference.</param>
    /// <returns>The capture outcome.</returns>
    public static ExternalAuthSessionCapture Captured(ExternalAuthSessionReference reference) =>
        new(reference, ExternalAuthErrorCode.None);

    /// <summary>Builds a typed capture refusal.</summary>
    /// <param name="code">The refusal code.</param>
    /// <returns>The capture outcome.</returns>
    public static ExternalAuthSessionCapture Refused(ExternalAuthErrorCode code) =>
        new(null, code);
}

/// <summary>
/// Server-side authority over authenticated sessions, implemented by the
/// host against its real authorization path (the server's authorization
/// context and user manager). The seam calls it to capture and re-resolve
/// the originating session of a link flow; the implementation classifies the
/// origin of every issued session reference (local, API key, external).
/// </summary>
public interface IExternalAuthSessionAuthority
{
    /// <summary>
    /// Captures the authenticated session of the current request, if any.
    /// A request that presented an explicit credential that was rejected
    /// must be refused; it never falls back to an anonymous or cookie
    /// identity.
    /// </summary>
    /// <param name="httpContext">The initiating request.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The captured reference or a typed refusal.</returns>
    ValueTask<ExternalAuthSessionCapture> CaptureAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-resolves a previously recorded session reference: the exact token
    /// must still resolve to the exact user and device, and the user must
    /// not be disabled. External-origin references are refused.
    /// </summary>
    /// <param name="reference">The reference recorded at initiation.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The typed revalidation verdict.</returns>
    ValueTask<ExternalAuthSessionRevalidation> RevalidateAsync(
        ExternalAuthSessionReference reference,
        CancellationToken cancellationToken);
}
