// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlopTank.ExternalAuth;

/// <summary>
/// Decides whether the current request transport may carry correlation
/// cookies. The accepted rule is an actually-trusted HTTPS scheme (the
/// forwarded-headers middleware has already run when this is evaluated, so
/// IsHttps reflects validated X-Forwarded-Proto from a trusted proxy), not a
/// peer-address shortcut. Plain HTTP is allowed only when the host runs an
/// explicit development deployment AND the operator explicitly opted in via
/// the environment variable; an opt-in on a non-development deployment is
/// rejected loudly and never enables anything.
/// </summary>
public sealed class ExternalAuthTransportPolicy
{
    /// <summary>The environment variable that opts into development
    /// HTTP.</summary>
    public const string DevHttpEnvironmentVariable = "SLOPTANK_EXTERNALAUTH_DEV_HTTP";

    private readonly ILogger<ExternalAuthTransportPolicy>? _logger;
    private readonly bool _devHttpActive;
    private readonly bool _devHttpRejectedInProduction;

    /// <summary>Initializes the policy; evaluated once.</summary>
    /// <param name="hostEnvironment">The host environment.</param>
    /// <param name="logger">Optional logger.</param>
    public ExternalAuthTransportPolicy(
        IHostEnvironment hostEnvironment,
        ILogger<ExternalAuthTransportPolicy>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        _logger = logger;
        HostEnvironmentName = hostEnvironment.EnvironmentName;

        var optedIn = string.Equals(
            Environment.GetEnvironmentVariable(DevHttpEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        DevHttpOptIn = optedIn;

        var isDevelopment = hostEnvironment.IsDevelopment();
        if (optedIn && isDevelopment)
        {
            _devHttpActive = true;
            _logger?.LogWarning(
                "External-auth development HTTP profile ACTIVE (environment {Env}); correlation cookies may be minted over plain HTTP. This must never be true of a production deployment.",
                HostEnvironmentName);
        }
        else if (optedIn && !isDevelopment)
        {
            _devHttpRejectedInProduction = true;
            _logger?.LogError(
                "External-auth development HTTP opt-in ({Variable}=1) REJECTED: host environment {Env} is not Development. Plain-HTTP cookie minting stays disabled.",
                DevHttpEnvironmentVariable,
                HostEnvironmentName);
        }
    }

    /// <summary>Gets the host environment name the policy was evaluated
    /// against.</summary>
    public string HostEnvironmentName { get; }

    /// <summary>Gets a value indicating whether the operator set the
    /// development HTTP opt-in variable.</summary>
    public bool DevHttpOptIn { get; }

    /// <summary>Gets a value indicating whether development plain-HTTP
    /// minting is actually active (explicit dev deployment AND opt-in).
    /// </summary>
    public bool DevHttpActive => _devHttpActive;

    /// <summary>Gets a value indicating whether an opt-in was supplied but
    /// rejected because the host is not a development deployment.</summary>
    public bool DevHttpRejectedInProduction => _devHttpRejectedInProduction;

    /// <summary>
    /// Gets a value indicating whether correlation cookies may be minted for
    /// this request: an actually secure scheme, or the active development
    /// profile.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>True when cookie minting is allowed.</returns>
    public bool IsSecureTransport(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Request.IsHttps || _devHttpActive;
    }
}
