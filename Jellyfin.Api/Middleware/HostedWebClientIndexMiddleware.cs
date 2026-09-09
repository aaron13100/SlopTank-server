// SlopTank modification notice: added or changed by SlopTank on 2026-08-13, 2026-09-09.
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>Serves the hosted SPA shell with a BaseUrl-aware fixed web base.</summary>
public sealed class HostedWebClientIndexMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _webPath;
    private readonly ILogger<HostedWebClientIndexMiddleware> _logger;

    /// <summary>Initializes a new instance of the <see cref="HostedWebClientIndexMiddleware"/> class.</summary>
    /// <param name="next">The next delegate in the request pipeline.</param>
    /// <param name="webPath">The deployed web client root.</param>
    /// <param name="logger">Logger for a shell that cannot be read or is malformed.</param>
    public HostedWebClientIndexMiddleware(
        RequestDelegate next,
        string webPath,
        ILogger<HostedWebClientIndexMiddleware> logger)
    {
        _next = next;
        _webPath = webPath;
        _logger = logger;
    }

    /// <summary>Serves /web/index.html after default-file rewriting.</summary>
    /// <param name="context">The request being answered.</param>
    /// <returns>A <see cref="Task"/> that completes once the request has been handled.</returns>
    public async Task Invoke(HttpContext context)
    {
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.Request.Path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            await HostedWebShell.WriteAsync(context, _webPath, _logger).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}
