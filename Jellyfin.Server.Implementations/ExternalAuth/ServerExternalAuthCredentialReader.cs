// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Server.Implementations.Security;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using SlopTank.ExternalAuth;

namespace Jellyfin.Server.Implementations.ExternalAuth;

/// <summary>
/// The server-backed shared auth-transport reader: enumerates every enabled
/// credential transport that presents a value on the request, mirroring
/// <see cref="AuthorizationContext"/> exactly (including the
/// <c>EnableLegacyAuthorization</c> gates). Both the session authority and
/// the cookie transport consume this one reader, so no credential parser
/// can drift between capture and transport precedence.
/// </summary>
public sealed class ServerExternalAuthCredentialReader : IExternalAuthCredentialReader
{
    private readonly IServerConfigurationManager _configurationManager;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ServerExternalAuthCredentialReader"/> class.
    /// </summary>
    /// <param name="configurationManager">The server configuration (the
    /// legacy-authorization gate).</param>
    public ServerExternalAuthCredentialReader(IServerConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<PresentedExternalCredential> Enumerate(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var legacy = _configurationManager.Configuration.EnableLegacyAuthorization;
        var presented = new List<PresentedExternalCredential>();

        var authorizationHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            var fields = ParseAuthorizationHeader(authorizationHeader, legacy);
            if (fields is null)
            {
                presented.Add(new PresentedExternalCredential(ExternalAuthTransport.AuthorizationHeader, null, Malformed: true));
            }
            else if (fields.TryGetValue("Token", out var token) && !string.IsNullOrEmpty(token))
            {
                presented.Add(new PresentedExternalCredential(ExternalAuthTransport.AuthorizationHeader, token, Malformed: false));
            }
            else
            {
                // A MediaBrowser header without a Token parameter carries
                // client identity only; the authorization context treats it
                // as no credential, and so does this reader.
                presented.Add(new PresentedExternalCredential(ExternalAuthTransport.AuthorizationHeader, null, Malformed: false));
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
                    presented.Add(new PresentedExternalCredential(ExternalAuthTransport.LegacyHeader, null, Malformed: fields is null));
                }
                else
                {
                    presented.Add(new PresentedExternalCredential(ExternalAuthTransport.LegacyHeader, token, Malformed: false));
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
        // credentials; drop them (the cookie transport must not treat a
        // bare client-identity Authorization header as an explicit
        // credential, exactly as the authorization context does not).
        return presented
            .Where(static c => c.Malformed || c.Token is not null || c.Transport == ExternalAuthTransport.ApiKeyQuery)
            .ToList();
    }

    private static void AddPlainTokenHeader(
        List<PresentedExternalCredential> presented,
        HttpRequest request,
        string headerName,
        ExternalAuthTransport transport)
    {
        var value = request.Headers[headerName].ToString();
        if (!string.IsNullOrEmpty(value))
        {
            presented.Add(new PresentedExternalCredential(transport, value, Malformed: false));
        }
    }

    private static void AddApiKeyQuery(
        List<PresentedExternalCredential> presented,
        HttpRequest request,
        string name)
    {
        if (request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
        {
            presented.Add(new PresentedExternalCredential(ExternalAuthTransport.ApiKeyQuery, value.ToString(), Malformed: false));
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
}
