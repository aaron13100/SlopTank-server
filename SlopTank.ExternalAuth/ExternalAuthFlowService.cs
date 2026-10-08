// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.AspNetCore.Http;

using System.Security.Cryptography;
using System.Text;

namespace SlopTank.ExternalAuth;

/// <summary>Options of the pending-flow correlation store.</summary>
public sealed record ExternalAuthFlowOptions
{
    /// <summary>Hard bound on concurrently pending flows server-wide.
    /// Deterministic: at the bound with nothing expired, new flows are
    /// refused with <see cref="ExternalAuthErrorCode.TooManyPendingFlows"/>
    /// rather than evicting a live flow.</summary>
    public int MaxPendingFlows { get; init; } = 256;

    /// <summary>Age bound of a pending flow. Expired entries are evicted
    /// lazily on every access and the matching cookie is expired with the
    /// same lifetime.</summary>
    public TimeSpan FlowTimeToLive { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Prefix of every per-flow correlation cookie name. The flow
    /// reference is appended; no other cookie is read or cleared.</summary>
    public string FlowCookieNamePrefix { get; init; } = "sloptank-extauth-fx-";
}

/// <summary>A pending browser flow.</summary>
public sealed record ExternalAuthFlowRecord
{
    /// <summary>Gets the opaque base64url flow reference (also the cookie
    /// name suffix).</summary>
    public required string FlowReference { get; init; }

    /// <summary>Gets the full per-flow cookie name.</summary>
    public required string CookieName { get; init; }

    /// <summary>Gets the SHA-256 hash of the correlation secret carried by
    /// the cookie; the plaintext is never stored.</summary>
    public required string CorrelationSecretHash { get; init; }

    /// <summary>Gets the provider instance this flow was started against.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>Gets the provider configuration epoch the flow was started
    /// under.</summary>
    public required long ProviderEpoch { get; init; }

    /// <summary>Gets the flow mode.</summary>
    public required ExternalLoginFlowMode Mode { get; init; }

    /// <summary>Gets the recorded originating session reference for link
    /// flows; null for login flows.</summary>
    public ExternalAuthSessionReference? SessionReference { get; init; }

    /// <summary>Gets the client identity the begin request supplied, so a
    /// completed login flow can mint its session under the client fields
    /// the browser actually presented. Carries no credential.</summary>
    public ExternalLoginClient? Client { get; init; }

    /// <summary>Gets the creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the expiry time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Outcome of beginning a flow.</summary>
public sealed record ExternalAuthBeginOutcome
{
    private ExternalAuthBeginOutcome(
        ExternalAuthFlowRecord? flow,
        string? correlationSecret,
        ExternalAuthErrorCode refusal,
        string? detail)
    {
        Flow = flow;
        CorrelationSecret = correlationSecret;
        RefusalCode = refusal;
        RefusalDetail = detail;
    }

    /// <summary>Gets the started flow on success; null otherwise.</summary>
    public ExternalAuthFlowRecord? Flow { get; }

    /// <summary>Gets the one-time correlation secret plaintext, handed to
    /// the provider exactly once at begin so it can bind its own pending
    /// record by hash. The seam persists only the hash; this value is never
    /// logged or stored by the seam after construction.</summary>
    public string? CorrelationSecret { get; }

    /// <summary>Gets the typed refusal code;
    /// <see cref="ExternalAuthErrorCode.None"/> on success.</summary>
    public ExternalAuthErrorCode RefusalCode { get; }

    /// <summary>Gets a sanitized refusal detail token; never carries
    /// credentials.</summary>
    public string? RefusalDetail { get; }

    /// <summary>Gets a value indicating whether the flow started.</summary>
    public bool IsStarted => Flow is not null;

    /// <summary>Builds a started outcome.</summary>
    /// <param name="flow">The pending flow record.</param>
    /// <param name="correlationSecret">The one-time secret plaintext for
    /// the provider.</param>
    /// <returns>The outcome.</returns>
    public static ExternalAuthBeginOutcome Started(ExternalAuthFlowRecord flow, string correlationSecret) =>
        new(flow, correlationSecret, ExternalAuthErrorCode.None, null);

    /// <summary>Builds a typed refusal.</summary>
    /// <param name="code">The refusal code.</param>
    /// <param name="detail">Optional sanitized detail.</param>
    /// <returns>The outcome.</returns>
    public static ExternalAuthBeginOutcome Refused(ExternalAuthErrorCode code, string? detail = null) =>
        new(null, null, code, detail);
}

/// <summary>Outcome of consuming a flow at callback time.</summary>
public sealed record ExternalAuthConsumeOutcome
{
    private ExternalAuthConsumeOutcome(ExternalAuthFlowRecord? flow, ExternalAuthErrorCode refusal, string? detail)
    {
        Flow = flow;
        RefusalCode = refusal;
        RefusalDetail = detail;
    }

    /// <summary>Gets the consumed flow record on success; null
    /// otherwise.</summary>
    public ExternalAuthFlowRecord? Flow { get; }

    /// <summary>Gets the typed refusal code;
    /// <see cref="ExternalAuthErrorCode.None"/> on success.</summary>
    public ExternalAuthErrorCode RefusalCode { get; }

    /// <summary>Gets a sanitized refusal detail token.</summary>
    public string? RefusalDetail { get; }

    /// <summary>Gets a value indicating whether the flow was consumed with a
    /// proven browser binding.</summary>
    public bool IsConsumed => Flow is not null;

    /// <summary>Builds a consumed outcome.</summary>
    /// <param name="flow">The consumed flow record.</param>
    /// <returns>The outcome.</returns>
    public static ExternalAuthConsumeOutcome Consumed(ExternalAuthFlowRecord flow) =>
        new(flow, ExternalAuthErrorCode.None, null);

    /// <summary>Builds a typed refusal.</summary>
    /// <param name="code">The refusal code.</param>
    /// <param name="detail">Optional sanitized detail.</param>
    /// <returns>The outcome.</returns>
    public static ExternalAuthConsumeOutcome Refused(ExternalAuthErrorCode code, string? detail = null) =>
        new(null, code, detail);
}

/// <summary>
/// Per-flow browser correlation: each pending flow gets its own opaque
/// reference and its own HttpOnly Secure SameSite=None cookie; consuming a
/// flow proves the callback arrived in the browser that started it and
/// clears only that flow's cookie. The server-side store is bounded by hard
/// count and age with deterministic refusal and lazy eviction.
/// </summary>
public interface IExternalAuthFlowService
{
    /// <summary>Gets the configured options.</summary>
    ExternalAuthFlowOptions Options { get; }

    /// <summary>
    /// Starts a flow: validates the transport may carry correlation cookies,
    /// generates the flow reference and secret, records the pending flow and
    /// sets the per-flow cookie on the response.
    /// </summary>
    /// <param name="httpContext">The begin request.</param>
    /// <param name="providerId">The provider instance id.</param>
    /// <param name="providerEpoch">The provider configuration epoch.</param>
    /// <param name="mode">The flow mode.</param>
    /// <param name="sessionReference">The captured originating session for
    /// link flows; must be null for login flows.</param>
    /// <param name="client">The client identity the begin request
    /// supplied; recorded on the flow for the completing callback.</param>
    /// <returns>The started flow or a typed refusal.</returns>
    ExternalAuthBeginOutcome BeginFlow(
        HttpContext httpContext,
        string providerId,
        long providerEpoch,
        ExternalLoginFlowMode mode,
        ExternalAuthSessionReference? sessionReference,
        ExternalLoginClient? client = null);

    /// <summary>
    /// Consumes a flow at callback time: looks up the pending record,
    /// evicting it as expired; proves the browser binding by hashing the
    /// per-flow cookie value and comparing in constant time; on a proven
    /// binding removes the record (single use) and expires exactly that
    /// flow's cookie. An unproven binding refuses without consuming, so a
    /// stolen callback cannot also deny the legitimate browser.
    /// </summary>
    /// <param name="httpContext">The callback request.</param>
    /// <param name="flowReference">The flow reference the callback belongs
    /// to.</param>
    /// <returns>The consumed flow record or a typed refusal.</returns>
    ExternalAuthConsumeOutcome ConsumeFlow(
        HttpContext httpContext,
        string flowReference);

    /// <summary>Gets the number of currently pending flows (expired entries
    /// not yet swept still count).</summary>
    int PendingCount { get; }

    /// <summary>Evicts every expired pending entry. Called lazily on begin
    /// and consume; exposed for tests and sweeps.</summary>
    /// <returns>The number of entries evicted.</returns>
    int SweepExpired();
}

/// <summary>Default <see cref="IExternalAuthFlowService"/>.</summary>
public sealed class ExternalAuthFlowService : IExternalAuthFlowService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExternalAuthFlowRecord> _flows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly ExternalAuthTransportPolicy _transportPolicy;

    /// <summary>Initializes the service.</summary>
    /// <param name="transportPolicy">The cookie transport policy.</param>
    /// <param name="options">The store options.</param>
    /// <param name="clock">The time source.</param>
    public ExternalAuthFlowService(
        ExternalAuthTransportPolicy transportPolicy,
        ExternalAuthFlowOptions? options = null,
        TimeProvider? clock = null)
    {
        _transportPolicy = transportPolicy;
        Options = options ?? new ExternalAuthFlowOptions();
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ExternalAuthFlowOptions Options { get; }

    /// <inheritdoc />
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _flows.Count;
            }
        }
    }

    /// <inheritdoc />
    public ExternalAuthBeginOutcome BeginFlow(
        HttpContext httpContext,
        string providerId,
        long providerEpoch,
        ExternalLoginFlowMode mode,
        ExternalAuthSessionReference? sessionReference,
        ExternalLoginClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        }

        if (mode == ExternalLoginFlowMode.Login && sessionReference is not null)
        {
            throw new ArgumentException("Login flows carry no session reference.");
        }

        if (mode == ExternalLoginFlowMode.Link && sessionReference is null)
        {
            throw new ArgumentException("Link flows require a captured session reference.");
        }

        if (!_transportPolicy.IsSecureTransport(httpContext))
        {
            return ExternalAuthBeginOutcome.Refused(
                ExternalAuthErrorCode.TransportInsecure,
                "correlation cookies require HTTPS or the explicit development profile");
        }

        var now = _clock.GetUtcNow();
        SweepExpired(now);

        string flowReference;
        string secret;
        lock (_sync)
        {
            if (_flows.Count >= Options.MaxPendingFlows)
            {
                return ExternalAuthBeginOutcome.Refused(
                    ExternalAuthErrorCode.TooManyPendingFlows,
                    $"pending flow bound {Options.MaxPendingFlows} reached");
            }

            (flowReference, secret) = NewFlowMaterial();
            var cookieName = Options.FlowCookieNamePrefix + flowReference;
            _flows[flowReference] = new ExternalAuthFlowRecord
            {
                FlowReference = flowReference,
                CookieName = cookieName,
                CorrelationSecretHash = HashSecret(secret),
                ProviderId = providerId,
                ProviderEpoch = providerEpoch,
                Mode = mode,
                SessionReference = sessionReference,
                Client = client,
                CreatedAt = now,
                ExpiresAt = now + Options.FlowTimeToLive,
            };
        }

        // The cookie value is the correlation secret itself; the server
        // stores only its hash. Attributes are exactly the cross-site
        // callback contract: HttpOnly, Secure, SameSite=None, host-only.
        httpContext.Response.Cookies.Append(
            Options.FlowCookieNamePrefix + flowReference,
            secret,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                IsEssential = true,
                Path = "/",
                MaxAge = Options.FlowTimeToLive,
            });

        lock (_sync)
        {
            return _flows.TryGetValue(flowReference, out var record)
                ? ExternalAuthBeginOutcome.Started(record, secret)
                : ExternalAuthBeginOutcome.Refused(ExternalAuthErrorCode.FlowUnknown, "race");
        }
    }

    /// <inheritdoc />
    public ExternalAuthConsumeOutcome ConsumeFlow(
        HttpContext httpContext,
        string flowReference)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (string.IsNullOrEmpty(flowReference))
        {
            return ExternalAuthConsumeOutcome.Refused(ExternalAuthErrorCode.FlowUnknown, "flow-reference:absent");
        }

        var now = _clock.GetUtcNow();
        ExternalAuthFlowRecord? record;
        lock (_sync)
        {
            if (!_flows.TryGetValue(flowReference, out record!))
            {
                return ExternalAuthConsumeOutcome.Refused(ExternalAuthErrorCode.FlowUnknown, "flow-reference:unknown-or-consumed");
            }

            if (record.ExpiresAt <= now)
            {
                _flows.Remove(flowReference);
                ExpireCookie(httpContext, record.CookieName);
                return ExternalAuthConsumeOutcome.Refused(ExternalAuthErrorCode.FlowExpired, "flow:expired");
            }
        }

        // Prove the browser binding BEFORE consuming: a callback replayed
        // into another browser (or with cookies blocked) must refuse while
        // the legitimate browser can still complete.
        var cookieValue = httpContext.Request.Cookies[record.CookieName];
        if (cookieValue is null || !FixedTimeEquals(HashSecret(cookieValue), record.CorrelationSecretHash))
        {
            return ExternalAuthConsumeOutcome.Refused(
                ExternalAuthErrorCode.BrowserBindingFailed,
                "correlation cookie absent or mismatched");
        }

        lock (_sync)
        {
            // Single-use from the moment the binding is proven: a concurrent
            // duplicate callback loses the race here.
            if (!_flows.Remove(flowReference))
            {
                return ExternalAuthConsumeOutcome.Refused(ExternalAuthErrorCode.FlowUnknown, "flow:consumed-race");
            }
        }

        ExpireCookie(httpContext, record.CookieName);
        return ExternalAuthConsumeOutcome.Consumed(record);
    }

    /// <inheritdoc />
    public int SweepExpired()
    {
        lock (_sync)
        {
            return SweepExpired(_clock.GetUtcNow());
        }
    }

    private int SweepExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_flows.Count == 0)
            {
                return 0;
            }

            var expired = _flows.Where(kv => kv.Value.ExpiresAt <= now).ToList();
            foreach (var kv in expired)
            {
                _flows.Remove(kv.Key);
            }

            return expired.Count;
        }
    }

    private void ExpireCookie(HttpContext httpContext, string cookieName)
    {
        // Expires exactly this flow's cookie; every other pending flow's
        // cookie in the same browser is untouched.
        httpContext.Response.Cookies.Append(
            cookieName,
            string.Empty,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                IsEssential = true,
                Path = "/",
                Expires = DateTimeOffset.UnixEpoch,
            });
    }

    private static (string Reference, string Secret) NewFlowMaterial()
    {
        var reference = Base64Url(RandomNumberGenerator.GetBytes(32));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (reference, secret);
    }

    private static string HashSecret(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Convert.FromHexString(a);
        var right = Convert.FromHexString(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
