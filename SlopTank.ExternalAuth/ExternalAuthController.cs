// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.AspNetCore.Mvc;

namespace SlopTank.ExternalAuth;

/// <summary>
/// The provider-neutral external-login surface: discovery of active
/// providers, anonymous login-flow begin, and authenticated link-flow begin.
/// Callbacks stay in the provider plugin; this controller only starts flows
/// and never sees authorization codes or tokens.
/// </summary>
[ApiController]
[Route("Auth/External")]
public sealed class ExternalAuthController : ControllerBase
{
    private readonly IExternalLoginProviderRegistry _registry;
    private readonly IExternalAuthFlowService _flows;
    private readonly IExternalAuthSessionAuthority _sessionAuthority;
    private readonly ExternalAuthEndpointsOptions _options;

    /// <summary>Initializes the controller.</summary>
    /// <param name="registry">The provider registry.</param>
    /// <param name="flows">The per-flow correlation service.</param>
    /// <param name="sessionAuthority">The session capture authority.</param>
    /// <param name="options">Endpoint options.</param>
    public ExternalAuthController(
        IExternalLoginProviderRegistry registry,
        IExternalAuthFlowService flows,
        IExternalAuthSessionAuthority sessionAuthority,
        ExternalAuthEndpointsOptions options)
    {
        _registry = registry;
        _flows = flows;
        _sessionAuthority = sessionAuthority;
        _options = options;
    }

    /// <summary>
    /// Lists the active external-login providers for the login page. Only
    /// id, displayName and order ever cross this boundary; the envelope is
    /// additive-only from schemaVersion 1.
    /// </summary>
    /// <returns>The discovery document.</returns>
    [HttpGet("Providers")]
    public ActionResult<ExternalProviderDiscoveryDocument> GetProviders()
    {
        var providers = _registry.GetActiveProviders()
            .Select(static r => new ExternalProviderInfo(r.Descriptor.Id, r.Descriptor.DisplayName, r.Descriptor.Order))
            .ToList();
        return Ok(new ExternalProviderDiscoveryDocument(1, providers));
    }

    /// <summary>
    /// Begins an anonymous login flow: binds the browser with a fresh
    /// per-flow correlation cookie, asks the provider for its challenge
    /// (awaiting the real preparation with cancellation), and redirects.
    /// </summary>
    /// <param name="providerId">The provider instance id.</param>
    /// <param name="appName">Optional client app name.</param>
    /// <param name="appVersion">Optional client app version.</param>
    /// <param name="deviceId">Optional client device id.</param>
    /// <param name="deviceName">Optional client device name.</param>
    /// <returns>A redirect to the provider, or to the sanitized error
    /// target.</returns>
    [HttpGet("Begin/{providerId}")]
    public async Task<IActionResult> Begin(
        [FromRoute] string providerId,
        [FromQuery] string? appName,
        [FromQuery] string? appVersion,
        [FromQuery] string? deviceId,
        [FromQuery] string? deviceName)
    {
        var registration = _registry.GetActive(providerId);
        if (registration is null)
        {
            return ErrorRedirect(ExternalAuthErrorCode.ProviderUnknown);
        }

        var client = new ExternalLoginClient(appName, appVersion, deviceId, deviceName);
        return await BeginFlowAndChallengeAsync(registration, ExternalLoginFlowMode.Login, sessionReference: null, client)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Begins an explicit link flow. The request must carry an authenticated
    /// LOCAL session over an explicit credential transport; a presented but
    /// rejected credential is refused and never falls back to an anonymous or
    /// cookie identity. The captured session reference is retained in the
    /// pending flow and re-resolved at completion.
    /// </summary>
    /// <param name="providerId">The provider instance id.</param>
    /// <param name="appName">Optional client app name.</param>
    /// <param name="appVersion">Optional client app version.</param>
    /// <param name="deviceId">Optional client device id.</param>
    /// <param name="deviceName">Optional client device name.</param>
    /// <returns>A redirect to the provider, or to the sanitized error
    /// target.</returns>
    [HttpPost("BeginLink/{providerId}")]
    public async Task<IActionResult> BeginLink(
        [FromRoute] string providerId,
        [FromQuery] string? appName,
        [FromQuery] string? appVersion,
        [FromQuery] string? deviceId,
        [FromQuery] string? deviceName)
    {
        var registration = _registry.GetActive(providerId);
        if (registration is null)
        {
            return ErrorRedirect(ExternalAuthErrorCode.ProviderUnknown);
        }

        var capture = await _sessionAuthority.CaptureAsync(HttpContext, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!capture.IsCaptured)
        {
            // A refused explicit credential (or no credential at all) ends
            // here; there is no fallback transport to try.
            return ErrorRedirect(capture.RefusalCode == ExternalAuthErrorCode.None
                ? ExternalAuthErrorCode.LinkSessionInvalid
                : capture.RefusalCode);
        }

        var client = new ExternalLoginClient(appName, appVersion, deviceId, deviceName);
        return await BeginFlowAndChallengeAsync(
            registration,
            ExternalLoginFlowMode.Link,
            capture.Reference,
            client).ConfigureAwait(false);
    }

    private async Task<IActionResult> BeginFlowAndChallengeAsync(
        ExternalLoginProviderRegistration registration,
        ExternalLoginFlowMode mode,
        ExternalAuthSessionReference? sessionReference,
        ExternalLoginClient client)
    {
        var begin = _flows.BeginFlow(
            HttpContext,
            registration.Descriptor.Id,
            registration.Descriptor.Epoch,
            mode,
            sessionReference);
        if (!begin.IsStarted)
        {
            return ErrorRedirect(begin.RefusalCode);
        }

        ExternalLoginChallengeResult challenge;
        try
        {
            var context = new ExternalLoginContext(
                begin.Flow!.FlowReference,
                mode,
                begin.CorrelationSecret!,
                client,
                ReturnUrl: null);
            challenge = await registration.Provider.CreateChallengeAsync(context, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(499);
        }
#pragma warning disable CA1031 // a misbehaving provider must not crash the auth surface
        catch (Exception)
#pragma warning restore CA1031
        {
            return ErrorRedirect(ExternalAuthErrorCode.ChallengeFailed);
        }

        if (!challenge.IsSuccess || challenge.RedirectUrl is null)
        {
            return ErrorRedirect(challenge.ErrorCode == ExternalAuthErrorCode.None
                ? ExternalAuthErrorCode.ChallengeFailed
                : challenge.ErrorCode);
        }

        return Redirect(challenge.RedirectUrl);
    }

    private IActionResult ErrorRedirect(ExternalAuthErrorCode code)
    {
        // The wire value is the stable enum member name; it is part of the
        // additive-only version 1 contract.
        return Redirect(_options.ErrorRedirectBase + code.ToString());
    }
}

/// <summary>Discovery document of active providers.</summary>
/// <param name="SchemaVersion">The envelope schema version.</param>
/// <param name="Providers">The active providers.</param>
public sealed record ExternalProviderDiscoveryDocument(
    int SchemaVersion,
    IReadOnlyList<ExternalProviderInfo> Providers);

/// <summary>One active provider entry. Additive-only from version 1.</summary>
/// <param name="Id">The stable provider instance id.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Order">The discovery ordering.</param>
public sealed record ExternalProviderInfo(
    string Id,
    string DisplayName,
    int Order);
