// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
#pragma warning disable CS1591
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Api.Auth.PlaybackPreparationWorkerPolicy;

/// <summary>Authorizes only API-key calls arriving from loopback.</summary>
public sealed class PlaybackPreparationWorkerHandler : AuthorizationHandler<PlaybackPreparationWorkerRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PlaybackPreparationWorkerHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlaybackPreparationWorkerRequirement requirement)
    {
        var ip = _httpContextAccessor.HttpContext?.GetNormalizedRemoteIP();
        var httpContext = _httpContextAccessor.HttpContext;
        var host = httpContext?.Request.Host.Host;
        var directLoopbackHost = string.Equals(host, "localhost", System.StringComparison.OrdinalIgnoreCase)
            || (host is not null && IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));
        var hasForwardingHeaders = httpContext?.Request.Headers.ContainsKey("Forwarded") == true
            || httpContext?.Request.Headers.ContainsKey("X-Forwarded-For") == true
            || httpContext?.Request.Headers.ContainsKey("X-Forwarded-Host") == true
            || httpContext?.Request.Headers.ContainsKey("X-Original-For") == true
            || httpContext?.Request.Headers.ContainsKey("X-Original-Host") == true;
        if (context.User.GetIsApiKey()
            && ip is not null
            && IPAddress.IsLoopback(ip)
            && directLoopbackHost
            && !hasForwardingHeaders)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail();
        }

        return Task.CompletedTask;
    }
}
