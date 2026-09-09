// SlopTank modification notice: added or changed by SlopTank on 2026-08-13, 2026-09-09.
using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Serves the hosted web client's SPA shell with a BaseUrl-aware fixed web base.
/// </summary>
internal static class HostedWebShell
{
    private const string BaseElement = "<base data-sloptank-web-base href=\"/web/\">";

    /// <summary>
    /// Reads the deployed shell and writes it to the response.
    /// </summary>
    /// <remarks>
    /// Read per request rather than cached. A deploy repoints the web root
    /// symlink underneath the running server, and the shell names
    /// content-hashed bundles, so a shell held from a previous release asks the
    /// browser for scripts the current release does not contain. Holding it in
    /// a field took the site down on every deploy until somebody restarted the
    /// server by hand.
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <param name="webPath">The deployed web client root.</param>
    /// <param name="logger">Logger for a shell that cannot be read or is malformed.</param>
    /// <returns>A <see cref="Task"/> that completes once the response has been written.</returns>
    internal static async Task WriteAsync(HttpContext context, string webPath, ILogger logger)
    {
        var indexPath = Path.Combine(webPath, "index.html");
        string template;
        try
        {
            template = await File.ReadAllTextAsync(indexPath, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning(ex, "No hosted web client shell at {IndexPath} for {RequestPath}.", indexPath, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        catch (IOException ex)
        {
            // A deploy unlinks the web root before it relinks it, so a request
            // landing inside that window is genuinely retryable.
            logger.LogWarning(ex, "The hosted web client shell at {IndexPath} could not be read for {RequestPath}.", indexPath, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var markerIndex = template.IndexOf(BaseElement, StringComparison.Ordinal);
        if (markerIndex < 0 || markerIndex != template.LastIndexOf(BaseElement, StringComparison.Ordinal))
        {
            logger.LogError(
                "The hosted web client shell at {IndexPath} must contain exactly one {BaseElement} element.",
                indexPath,
                BaseElement);
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
