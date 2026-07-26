using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Redirects pretty permalink entry paths into the web client's hash router.
/// </summary>
public sealed class PermalinkRedirectionMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkRedirectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    public PermalinkRedirectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Redirects supported read requests without interpreting the opaque permalink identifier.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>The asynchronous middleware task.</returns>
    public async Task Invoke(HttpContext httpContext)
    {
        if (!TryGetHashRoute(httpContext.Request.Path, out var hashRoute))
        {
            await _next(httpContext).ConfigureAwait(false);
            return;
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(httpContext.Request.Method)
            && !HttpMethods.IsHead(httpContext.Request.Method))
        {
            httpContext.Response.Headers.Allow = "GET, HEAD";
            httpContext.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var location = string.Concat(
            httpContext.Request.PathBase.Value,
            "/web/#/",
            hashRoute,
            httpContext.Request.QueryString.Value);
        httpContext.Response.Redirect(location);
    }

    private static bool TryGetHashRoute(PathString path, out string? hashRoute)
    {
        if (path.StartsWithSegments("/web/p", out var remaining)
            && remaining.HasValue
            && !remaining.Equals("/", StringComparison.Ordinal))
        {
            hashRoute = string.Concat("p", remaining.Value);
            return true;
        }

        if (path.StartsWithSegments("/web/w", out remaining)
            && remaining.HasValue
            && !remaining.Equals("/", StringComparison.Ordinal))
        {
            hashRoute = string.Concat("w", remaining.Value);
            return true;
        }

        hashRoute = null;
        return false;
    }
}
