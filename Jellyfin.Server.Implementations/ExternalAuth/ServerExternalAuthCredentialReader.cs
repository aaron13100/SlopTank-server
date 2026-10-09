// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.

using System;
using System.Collections.Generic;
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
        if (request.Headers.ContainsKey("Authorization"))
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
                // The header was explicitly supplied but did not carry a
                // token. It remains presented and malformed for precedence:
                // an ambient browser cookie must never replace any explicit
                // Authorization value, including ordinary client metadata.
                presented.Add(new PresentedExternalCredential(ExternalAuthTransport.AuthorizationHeader, null, Malformed: true));
            }
        }

        if (legacy)
        {
            var legacyHeader = request.Headers["X-Emby-Authorization"].ToString();
            if (request.Headers.ContainsKey("X-Emby-Authorization"))
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

        return presented;
    }

    private static void AddPlainTokenHeader(
        List<PresentedExternalCredential> presented,
        HttpRequest request,
        string headerName,
        ExternalAuthTransport transport)
    {
        if (request.Headers.TryGetValue(headerName, out var values))
        {
            var value = values.ToString();
            presented.Add(new PresentedExternalCredential(transport, string.IsNullOrEmpty(value) ? null : value, Malformed: string.IsNullOrEmpty(value)));
        }
    }

    private static void AddApiKeyQuery(
        List<PresentedExternalCredential> presented,
        HttpRequest request,
        string name)
    {
        if (request.Query.TryGetValue(name, out var value))
        {
            var token = value.ToString();
            presented.Add(new PresentedExternalCredential(
                ExternalAuthTransport.ApiKeyQuery,
                string.IsNullOrEmpty(token) ? null : token,
                Malformed: string.IsNullOrEmpty(token)));
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
