// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SlopTank.ExternalAuth;

/// <summary>
/// One credential presented on a request, enumerated by the shared
/// transport reader. <see cref="Malformed"/> credentials count as
/// presented: an explicitly supplied (even invalid) credential must never
/// fall back to the cookie transport.
/// </summary>
/// <param name="Transport">The transport the credential arrived on.</param>
/// <param name="Token">The opaque token value, when the transport carries
/// one; never logged.</param>
/// <param name="Malformed">The transport presented a value the server
/// cannot parse.</param>
public sealed record PresentedExternalCredential(
    ExternalAuthTransport Transport,
    string? Token,
    bool Malformed);

/// <summary>
/// The shared auth-transport reader: enumerates every enabled credential
/// transport that presents a value on the request, exactly as the server's
/// authorization path sees them. The session authority and the cookie
/// transport both consume this one reader, so no credential parser can
/// drift between them.
/// </summary>
public interface IExternalAuthCredentialReader
{
    /// <summary>
    /// Enumerates the credentials presented on the request, highest
    /// precedence first. Transports gated by the server's legacy-
    /// authorization switch are enumerated only when that switch is on.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The presented credentials; empty when none.</returns>
    IReadOnlyList<PresentedExternalCredential> Enumerate(HttpRequest request);
}

/// <summary>
/// The cookie transport of minted external sessions. Placed AFTER
/// UseWebSockets and BEFORE authentication/handlers, it is a pure
/// translation layer: when the request presents ANY explicit credential
/// (including a malformed or revoked one) it does nothing at all, so
/// explicit transports keep their exact ordinary behavior and precedence.
/// Only a request with no explicit credential and a live opaque session
/// cookie is authenticated, by injecting the server-side retained token of
/// that minted session through the standard Authorization transport the
/// real authorization path already reads. Cookie-authenticated unsafe
/// methods and WebSocket upgrades require an exact same-origin Origin
/// header, including same-site foreign origins.
/// </summary>
public sealed class ExternalAuthCookieTransportMiddleware : IDisposable
{
    private readonly RequestDelegate _next;
    private readonly IExternalAuthCredentialReader _credentialReader;
    private readonly ExternalAuthSessionMintStore _mintStore;
    private readonly ExternalAuthTransportPolicy _transportPolicy;
    private readonly ILogger<ExternalAuthCookieTransportMiddleware>? _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<HttpContext, byte>> _openChannels = new(StringComparer.Ordinal);
    private readonly Timer? _channelRevalidation;

    /// <summary>Initializes the middleware.</summary>
    /// <param name="next">The next pipeline stage.</param>
    /// <param name="credentialReader">The shared transport reader.</param>
    /// <param name="mintStore">The external-session mint store.</param>
    /// <param name="transportPolicy">The cookie transport policy.</param>
    /// <param name="logger">Optional logger.</param>
    public ExternalAuthCookieTransportMiddleware(
        RequestDelegate next,
        IExternalAuthCredentialReader credentialReader,
        ExternalAuthSessionMintStore mintStore,
        ExternalAuthTransportPolicy transportPolicy,
        ILogger<ExternalAuthCookieTransportMiddleware>? logger = null)
    {
        _next = next;
        _credentialReader = credentialReader;
        _mintStore = mintStore;
        _transportPolicy = transportPolicy;
        _logger = logger;

        // Requirement 3 open-channel half: a revoked session (provider
        // disable/removal, explicit handle revocation) closes every open
        // cookie-authenticated channel immediately...
        _mintStore.SessionRevoked += AbortChannelsOf;
        // ...and every tracked channel is periodically re-resolved, so
        // expiry, logout and user disable (the host liveness probe) close
        // it too even without a revocation event.
        _channelRevalidation = new Timer(
            _ =>
            {
                try
                {
                    foreach (var handle in _openChannels.Keys)
                    {
                        if (_mintStore.Resolve(handle) is null)
                        {
                            AbortChannelsOf(handle);
                        }
                    }
                }
#pragma warning disable CA1031 // a background sweep must never take the timer down; the failure is logged and retried on the next tick
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Never silent: a failed sweep is logged and retried on
                    // the next tick. The catch is deliberately broad: a
                    // background sweep must never take the timer down.
                    _logger?.LogError(ex, "external cookie channel revalidation sweep failed");
                }
            },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    private void AbortChannelsOf(ExternalAuthMintedSession session) => AbortChannelsOf(session.Handle);

    private void AbortChannelsOf(string handle)
    {
        if (_openChannels.TryRemove(handle, out var channels))
        {
            foreach (var context in channels.Keys)
            {
                try
                {
                    context.Abort();
                }
#pragma warning disable CA1031 // a context that already closed must not break aborting its siblings
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger?.LogDebug(ex, "aborting a tracked cookie channel failed (already closed)");
                }
            }
        }
    }

    /// <summary>
    /// Gets the key under which the resolved minted session is published in
    /// <see cref="HttpContext.Items"/> for cookie-authenticated requests.
    /// </summary>
    public static readonly object CookieSessionItemKey = new();

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Requirement 4: any explicitly supplied enabled transport
        // (Authorization, X-Emby-Token, X-MediaBrowser-Token, api_key,
        // X-Emby-Authorization, ApiKey) takes precedence, INCLUDING
        // malformed or revoked values; the cookie is never a fallback.
        if (_credentialReader.Enumerate(context.Request).Count > 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var handle = context.Request.Cookies[_mintStore.Options.SessionCookieName];
        if (string.IsNullOrEmpty(handle))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var session = _mintStore.Resolve(handle);
        if (session is null)
        {
            // Unknown, expired or revoked handle: the cookie is worthless;
            // expire it and continue anonymously.
            ExpireSessionCookie(context);
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A cookie-authenticated request is a browser request: unsafe
        // methods and WebSocket upgrades must carry the exact same origin
        // (a same-site foreign origin is refused too).
        var upgrade = context.WebSockets.IsWebSocketRequest;
        var unsafeMethod = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method);
        if ((upgrade || unsafeMethod) && !IsSameOrigin(context.Request))
        {
            _logger?.LogInformation(
                "external cookie transport refused a {Method} request with foreign origin {Origin}",
                context.Request.Method,
                context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Publish the resolved session for downstream cookie-aware stages
        // (the open-channel tracker) and translate the cookie into the one
        // transport the real authorization path already reads: the standard
        // Authorization header, carrying only the server-side retained
        // token.
        context.Items[CookieSessionItemKey] = session;
        context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(
            $"MediaBrowser Token=\"{session.AccessToken}\", Client=\"{session.AppName}\", Device=\"{session.AppName}\", DeviceId=\"{session.DeviceId}\", Version=\"1.0\"");

        if (upgrade)
        {
            // Track the open channel for the lifetime of the connection so
            // revocation, expiry, logout and user disable can abort it.
            var channels = _openChannels.GetOrAdd(session.Handle, _ => new System.Collections.Concurrent.ConcurrentDictionary<HttpContext, byte>());
            channels[context] = 0;
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                channels.TryRemove(context, out _);
                if (channels.IsEmpty)
                {
                    _openChannels.TryRemove(session.Handle, out _);
                }
            }

            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channelRevalidation?.Dispose();
        _mintStore.SessionRevoked -= AbortChannelsOf;
    }

    /// <summary>
    /// Exact same-origin check of the browser Origin header against the
    /// request's own scheme and host (the forwarded-headers middleware has
    /// already run, so the scheme reflects validated proxy input).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the origin is exactly the request's own
    /// origin.</returns>
    internal static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Non-browser clients send no Origin at all; the cookie
            // transport is browser-only, so an absent Origin on an unsafe
            // cookie request is a refusal.
            return false;
        }

        var expected = $"{request.Scheme}://{request.Host}";
        return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);
    }

    private void ExpireSessionCookie(HttpContext context)
    {
        context.Response.Cookies.Append(
            _mintStore.Options.SessionCookieName,
            string.Empty,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !_transportPolicy.DevHttpActive,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/",
                Expires = DateTimeOffset.UnixEpoch,
            });
    }
}
