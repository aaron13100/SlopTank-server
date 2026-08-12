using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>Terminal document-navigation fallback for browser-router paths.</summary>
public sealed class HostedWebClientFallbackMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _webPath;
    private readonly ILogger<HostedWebClientFallbackMiddleware> _logger;

    /// <summary>Initializes a new instance of the <see cref="HostedWebClientFallbackMiddleware"/> class.</summary>
    /// <param name="next">The next delegate in the request pipeline.</param>
    /// <param name="webPath">The deployed web client root.</param>
    /// <param name="logger">Logger for a shell that cannot be read or is malformed.</param>
    public HostedWebClientFallbackMiddleware(
        RequestDelegate next,
        string webPath,
        ILogger<HostedWebClientFallbackMiddleware> logger)
    {
        _next = next;
        _webPath = webPath;
        _logger = logger;
    }

    /// <summary>Serves the shell only for a missing document navigation.</summary>
    /// <param name="context">The request being answered.</param>
    /// <returns>A <see cref="Task"/> that completes once the request has been handled.</returns>
    public async Task Invoke(HttpContext context)
    {
        if (IsDocumentNavigation(context.Request))
        {
            await HostedWebShell.WriteAsync(context, _webPath, _logger).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsDocumentNavigation(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        var extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension) && !extension.Equals(".html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Jellyfin's API controller roots are PascalCase. Never turn an API typo
        // into a successful HTML response, even when a caller sends Accept: text/html.
        var firstSegment = path.TrimStart('/').Split('/', 2)[0];
        if (firstSegment.Length > 0 && char.IsUpper(firstSegment[0]))
        {
            return false;
        }

        var fetchMode = request.Headers["Sec-Fetch-Mode"].ToString();
        var fetchDestination = request.Headers["Sec-Fetch-Dest"].ToString();
        if (fetchMode.Equals("navigate", StringComparison.OrdinalIgnoreCase)
            && fetchDestination.Equals("document", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return request.GetTypedHeaders().Accept?.Any(
            value => value.MediaType.Value?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true) == true;
    }
}
