// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SlopTank.ExternalAuth;

namespace Jellyfin.Server.Implementations.ExternalAuth;

/// <summary>
/// The server-backed external-session minter: mints a REAL server session
/// for a linked local user through <see cref="ISessionManager.AuthenticateDirect"/>
/// (the same direct path the server's own quick-connect feature uses, no
/// password involved), retains the access token server-side in the mint
/// store, and answers with an opaque HttpOnly cookie handle. Production
/// mints require an actually secure transport (validated forwarded headers
/// included); a refused handle (hard live cap reached) revokes the just
/// minted underlying session immediately, so a failed mint leaves no
/// orphaned server session behind.
/// </summary>
public sealed class ExternalAuthSessionMinter : IExternalAuthSessionMinter
{
    private readonly ISessionManager _sessionManager;
    private readonly ExternalAuthSessionMintStore _mintStore;
    private readonly ExternalAuthTransportPolicy _transportPolicy;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExternalAuthSessionMinter>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalAuthSessionMinter"/>
    /// class.
    /// </summary>
    /// <param name="sessionManager">The real session manager.</param>
    /// <param name="mintStore">The external-session mint store.</param>
    /// <param name="transportPolicy">The cookie transport policy.</param>
    /// <param name="clock">Optional time source.</param>
    /// <param name="logger">Optional logger.</param>
    public ExternalAuthSessionMinter(
        ISessionManager sessionManager,
        ExternalAuthSessionMintStore mintStore,
        ExternalAuthTransportPolicy transportPolicy,
        TimeProvider? clock = null,
        ILogger<ExternalAuthSessionMinter>? logger = null)
    {
        _sessionManager = sessionManager;
        _mintStore = mintStore;
        _transportPolicy = transportPolicy;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ExternalAuthMintResult> MintAsync(
        ExternalAuthMintRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrEmpty(request.AppName);
        ArgumentException.ThrowIfNullOrEmpty(request.AppVersion);
        ArgumentException.ThrowIfNullOrEmpty(request.DeviceId);
        ArgumentException.ThrowIfNullOrEmpty(request.DeviceName);

        // Requirement 7: a production mint requires an actually secure
        // transport; the development HTTP profile is the only exception and
        // is rejected loudly outside development deployments.
        if (!_transportPolicy.IsSecureTransport(httpContext))
        {
            return new ExternalAuthMintResult(ExternalAuthMintStatus.TransportInsecure, null);
        }

        AuthenticationResult authentication;
        try
        {
            authentication = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = request.UserId,
                Username = string.Empty,
                Password = string.Empty,
                App = request.AppName,
                AppVersion = request.AppVersion,
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                RemoteEndPoint = httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or SecurityException or InvalidOperationException)
        {
            _logger?.LogInformation(
                "external session mint refused by the session manager; user:{UserId} reason:{Reason}",
                request.UserId,
                ex.GetType().Name);
            return new ExternalAuthMintResult(ExternalAuthMintStatus.MintRefused, null);
        }

        if (string.IsNullOrEmpty(authentication.AccessToken) || authentication.User is null)
        {
            return new ExternalAuthMintResult(ExternalAuthMintStatus.MintRefused, null);
        }

        var now = _clock.GetUtcNow();
        var session = new ExternalAuthMintedSession(
            ExternalAuthSessionMintStore.NewHandle(),
            authentication.AccessToken,
            authentication.User.Id,
            request.DeviceId,
            request.AppName,
            request.ProviderId,
            request.ProviderEpoch,
            now,
            now + _mintStore.Options.SessionTimeToLive);

        if (!_mintStore.Insert(session))
        {
            // Failed-mint cleanup: the underlying server session exists but
            // has no handle; revoke it so nothing orphans.
            await _sessionManager.Logout(authentication.AccessToken).ConfigureAwait(false);
            return new ExternalAuthMintResult(ExternalAuthMintStatus.CapReached, null);
        }

        // The cookie carries ONLY the opaque handle; the token stays
        // server-side. Secure is unconditional in production (the dev HTTP
        // profile is the only case it is relaxed, and that profile cannot
        // be active outside development deployments).
        httpContext.Response.Cookies.Append(
            _mintStore.Options.SessionCookieName,
            session.Handle,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !_transportPolicy.DevHttpActive,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/",
                MaxAge = _mintStore.Options.SessionTimeToLive,
            });

        _logger?.LogInformation(
            "external session minted; user:{UserId} provider:{ProviderId} epoch:{Epoch}",
            session.UserId,
            session.ProviderId,
            session.ProviderEpoch);

        return new ExternalAuthMintResult(ExternalAuthMintStatus.Minted, session);
    }
}
