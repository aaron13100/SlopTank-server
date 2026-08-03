using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Api.Middleware;

/// <summary>Serves the hosted SPA shell with a BaseUrl-aware fixed web base.</summary>
public sealed class HostedWebClientIndexMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _indexTemplate;

    /// <summary>Initializes a new instance of the <see cref="HostedWebClientIndexMiddleware"/> class.</summary>
    public HostedWebClientIndexMiddleware(RequestDelegate next, string webPath)
    {
        _next = next;
        _indexTemplate = HostedWebShell.ReadTemplate(webPath);
    }

    /// <summary>Serves /web/index.html after default-file rewriting.</summary>
    public async Task Invoke(HttpContext context)
    {
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.Request.Path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            await HostedWebShell.WriteAsync(context, _indexTemplate).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>Terminal document-navigation fallback for browser-router paths.</summary>
public sealed class HostedWebClientFallbackMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _indexTemplate;

    /// <summary>Initializes a new instance of the <see cref="HostedWebClientFallbackMiddleware"/> class.</summary>
    public HostedWebClientFallbackMiddleware(RequestDelegate next, string webPath)
    {
        _next = next;
        _indexTemplate = HostedWebShell.ReadTemplate(webPath);
    }

    /// <summary>Serves the shell only for a missing document navigation.</summary>
    public async Task Invoke(HttpContext context)
    {
        if (IsDocumentNavigation(context.Request))
        {
            await HostedWebShell.WriteAsync(context, _indexTemplate).ConfigureAwait(false);
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

internal static class HostedWebShell
{
    private const string BaseElement = "<base data-sloptank-web-base href=\"/web/\">";

    internal static string? ReadTemplate(string webPath)
    {
        var indexPath = Path.Combine(webPath, "index.html");
        return File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
    }

    internal static async Task WriteAsync(HttpContext context, string? template)
    {
        if (template is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var markerIndex = template.IndexOf(BaseElement, StringComparison.Ordinal);
        if (markerIndex < 0 || markerIndex != template.LastIndexOf(BaseElement, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var webBase = string.Concat(context.Request.PathBase.Value?.TrimEnd('/'), "/web/");
        var replacement = string.Concat(
            "<base data-sloptank-web-base href=\"",
            WebUtility.HtmlEncode(webBase),
            "\">");
        var html = template.Replace(BaseElement, replacement, StringComparison.Ordinal);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(html);

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
