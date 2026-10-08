// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Security;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SlopTank.ExternalAuth;

namespace Jellyfin.Server.Implementations.ExternalAuth;

/// <summary>
/// The server-backed session authority of the external-login seam: captures and
/// re-resolves the originating authenticated local session through the same
/// credential transports and token stores the real authorization context uses.
/// The transport enumeration mirrors <see cref="AuthorizationContext"/> exactly,
/// including the <c>EnableLegacyAuthorization</c> gates: a transport the server
/// does not currently honor is invisible here too. Stricter than ordinary
/// request authorization (which resolves the first token it finds), capture for
/// linking fails closed: any presented-but-malformed credential, any
/// disagreement between simultaneously presented credentials, and any API-key
/// transport refuse the capture outright, and nothing ever falls back to a
/// cookie or anonymous identity.
/// </summary>
public sealed class ServerExternalAuthSessionAuthority : IExternalAuthSessionAuthority
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IDeviceManager _deviceManager;
    private readonly IUserManager _userManager;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly ILogger<ServerExternalAuthSessionAuthority> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ServerExternalAuthSessionAuthority"/> class.
    /// </summary>
    /// <param name="configurationManager">The server configuration (legacy-authorization gate).</param>
    /// <param name="deviceManager">The device (session token) store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="dbProvider">The database context factory (API keys).</param>
    /// <param name="logger">The logger.</param>
    public ServerExternalAuthSessionAuthority(
        IServerConfigurationManager configurationManager,
        IDeviceManager deviceManager,
        IUserManager userManager,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        ILogger<ServerExternalAuthSessionAuthority> logger)
    {
        _configurationManager = configurationManager;
        _deviceManager = deviceManager;
        _userManager = userManager;
        _dbProvider = dbProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ExternalAuthSessionCapture> CaptureAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var presented = EnumeratePresentedCredentials(httpContext.Request);

        if (presented.Count == 0)
        {
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        // API keys are explicit credentials but never a user link identity.
        if (presented.Any(static c => c.Transport is ExternalAuthTransport.ApiKeyQuery))
        {
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        // A presented-but-malformed credential never falls back to any other
        // transport, and two simultaneously presented user tokens that
        // disagree are refused rather than resolved by precedence.
        if (presented.Any(static c => c.Malformed))
        {
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        var tokens = presented.Select(static c => c.Token!).Distinct(StringComparer.Ordinal).ToList();
        if (tokens.Count != 1)
        {
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        var token = tokens[0];
        var transport = presented[0].Transport;
        var device = await ResolveDeviceAsync(token, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            // Unknown or revoked token, or a token that actually belongs to an
            // API key: presented-but-rejected, never a fallback.
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        var user = _userManager.GetUserById(device.UserId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return ExternalAuthSessionCapture.Refused(ExternalAuthErrorCode.LinkSessionInvalid);
        }

        _logger.LogInformation(
            "External-auth link session captured; transport:{Transport} device:{DeviceId}",
            transport,
            device.DeviceId);

        return ExternalAuthSessionCapture.Captured(new ExternalAuthSessionReference(
            device.UserId,
            device.DeviceId,
            device.AppName,
            transport,
            ExternalAuthSessionOrigin.Local,
            Fingerprint(token),
            ExternalAuthSessionToken.From(token),
            DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public async ValueTask<ExternalAuthSessionRevalidation> RevalidateAsync(
        ExternalAuthSessionReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var device = await ResolveDeviceAsync(reference.Token.Value, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            // Logged out or revoked between begin and callback.
            return ExternalAuthSessionRevalidation.Revoked;
        }

        if (device.UserId != reference.UserId
            || !string.Equals(device.DeviceId, reference.DeviceId, StringComparison.Ordinal))
        {
            // The token no longer resolves to the exact user and device the
            // reference recorded.
            return ExternalAuthSessionRevalidation.Mismatched;
        }

        var user = _userManager.GetUserById(device.UserId);
        if (user is null)
        {
            return ExternalAuthSessionRevalidation.Revoked;
        }

        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            return ExternalAuthSessionRevalidation.Disabled;
        }

        return ExternalAuthSessionRevalidation.Valid;
    }

    /// <summary>
    /// Enumerates every enabled credential transport that actually presents a
    /// value on the request, in the same precedence order
    /// <see cref="AuthorizationContext"/> resolves them. Transports gated by
    /// <c>EnableLegacyAuthorization</c> are only enumerated when that switch is
    /// on, mirroring the real authorization path; a transport presenting a
    /// value the server cannot parse (wrong scheme, no token in the header) is
    /// reported as malformed.
    /// </summary>
    /// <param name="request">The initiating request.</param>
    /// <returns>The presented credentials, highest precedence first.</returns>
    private List<PresentedCredential> EnumeratePresentedCredentials(HttpRequest request)
    {
        var legacy = _configurationManager.Configuration.EnableLegacyAuthorization;
        var presented = new List<PresentedCredential>();

        var authorizationHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            var fields = ParseAuthorizationHeader(authorizationHeader, legacy);
            if (fields is null)
            {
                presented.Add(new PresentedCredential(ExternalAuthTransport.AuthorizationHeader, null, Malformed: true));
            }
            else if (fields.TryGetValue("Token", out var token) && !string.IsNullOrEmpty(token))
            {
                presented.Add(new PresentedCredential(ExternalAuthTransport.AuthorizationHeader, token, Malformed: false));
            }
            else
            {
                // A MediaBrowser header without a Token parameter carries
                // client identity only; the authorization context treats it
                // as no credential, and so does capture.
                presented.Add(new PresentedCredential(ExternalAuthTransport.AuthorizationHeader, null, Malformed: false));
            }
        }

        if (legacy)
        {
            var legacyHeader = request.Headers["X-Emby-Authorization"].ToString();
            if (!string.IsNullOrEmpty(legacyHeader))
            {
                var fields = ParseAuthorizationHeader(legacyHeader, legacy);
                if (fields is null || !fields.TryGetValue("Token", out var token) || string.IsNullOrEmpty(token))
                {
                    presented.Add(new PresentedCredential(ExternalAuthTransport.LegacyHeader, null, Malformed: fields is null));
                }
                else
                {
                    presented.Add(new PresentedCredential(ExternalAuthTransport.LegacyHeader, token, Malformed: false));
                }
            }

            AddPlainTokenHeader(presented, request, "X-Emby-Token", ExternalAuthTransport.LegacyHeader);
            AddPlainTokenHeader(presented, request, "X-MediaBrowser-Token", ExternalAuthTransport.LegacyHeader);
        }

        AddApiKeyQuery(presented, request, "ApiKey");
        if (legacy)
        {
            AddApiKeyQuery(presented, request, "api_key");
        }

        // Entries that presented no credential value at all are not
        // credentials; drop them.
        return presented
            .Where(static c => c.Malformed || c.Token is not null || c.Transport == ExternalAuthTransport.ApiKeyQuery)
            .ToList();
    }

    private static void AddPlainTokenHeader(
        List<PresentedCredential> presented,
        HttpRequest request,
        string headerName,
        ExternalAuthTransport transport)
    {
        var value = request.Headers[headerName].ToString();
        if (!string.IsNullOrEmpty(value))
        {
            presented.Add(new PresentedCredential(transport, value, Malformed: false));
        }
    }

    private static void AddApiKeyQuery(
        List<PresentedCredential> presented,
        HttpRequest request,
        string name)
    {
        if (request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
        {
            // Recorded with its (opaque) value so a mixed API key and user
            // token can never be mistaken for a single credential; the value
            // itself is never logged.
            presented.Add(new PresentedCredential(ExternalAuthTransport.ApiKeyQuery, value.ToString(), Malformed: false));
        }
    }

    /// <summary>
    /// Parses a MediaBrowser/Emby authorization header with the real
    /// <see cref="AuthorizationContext"/> parser and scheme rules.
    /// </summary>
    /// <param name="headerValue">The raw header value.</param>
    /// <param name="legacy">Whether the legacy Emby scheme is enabled.</param>
    /// <returns>The parsed fields, or null when the scheme is not valid.</returns>
    private static Dictionary<string, string>? ParseAuthorizationHeader(string headerValue, bool legacy)
    {
        var firstSpace = headerValue.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace == -1)
        {
            return null;
        }

        var name = headerValue.AsSpan(0, firstSpace);
        var validName = name.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase)
            || (legacy && name.Equals("Emby", StringComparison.OrdinalIgnoreCase));
        if (!validName)
        {
            return null;
        }

        return AuthorizationContext.GetParts(headerValue.AsSpan(firstSpace + 1));
    }

    /// <summary>
    /// Resolves a raw token against the real stores, exactly as the
    /// authorization context does: a device (user session) first, then the API
    /// key table. API-key tokens are returned as null: they are credentials,
    /// but never a user session.
    /// </summary>
    /// <param name="token">The raw access token.</param>
    /// <param name="cancellationToken">Cancels the store lookup.</param>
    /// <returns>The device holding the session token, or null.</returns>
    private async Task<Device?> ResolveDeviceAsync(string token, CancellationToken cancellationToken)
    {
        var device = _deviceManager.GetDevices(
            new DeviceQuery { AccessToken = token }).Items.FirstOrDefault();
        if (device is not null)
        {
            return device;
        }

        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var isApiKey = await dbContext.ApiKeys
                .AsNoTracking()
                .AnyAsync(apiKey => apiKey.AccessToken == token, cancellationToken)
                .ConfigureAwait(false);
            if (isApiKey)
            {
                _logger.LogInformation("External-auth token resolution hit an API key; API keys are never link identities");
                return null;
            }
        }

        return null;
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    private sealed record PresentedCredential(
        ExternalAuthTransport Transport,
        string? Token,
        bool Malformed);
}
