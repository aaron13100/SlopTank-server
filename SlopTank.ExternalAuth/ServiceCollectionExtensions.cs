// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SlopTank.ExternalAuth;

/// <summary>
/// Endpoint-shape options of the external-login surface. The defaults are
/// the version 1 wire contract; hosts may override the error target only.
/// </summary>
public sealed record ExternalAuthEndpointsOptions
{
    /// <summary>The redirect target prefix for sanitized begin failures; the
    /// stable error-code name is appended.</summary>
    public string ErrorRedirectBase { get; init; } = "/web/#/login?extAuthError=";
}

/// <summary>
/// Registers the external-login seam services: the provider registry, the
/// transport policy, the per-flow correlation store and the endpoint
/// options. The session authority is host-provided (the server registers its
/// real authorization-context adapter; test hosts register their own).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the seam services to the collection.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureFlows">Optional flow-store overrides.</param>
    /// <param name="configureEndpoints">Optional endpoint overrides.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddSlopTankExternalAuth(
        this IServiceCollection services,
        Action<ExternalAuthFlowOptions>? configureFlows = null,
        Action<ExternalAuthEndpointsOptions>? configureEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var flowOptions = new ExternalAuthFlowOptions();
        configureFlows?.Invoke(flowOptions);
        var endpointOptions = new ExternalAuthEndpointsOptions();
        configureEndpoints?.Invoke(endpointOptions);

        services.TryAddSingleton(flowOptions);
        services.TryAddSingleton(endpointOptions);
        services.TryAddSingleton<IExternalLoginProviderRegistry, ExternalLoginProviderRegistry>();
        services.TryAddSingleton<ExternalAuthTransportPolicy>();
        services.TryAddSingleton<IExternalAuthFlowService>(sp => new ExternalAuthFlowService(
            sp.GetRequiredService<ExternalAuthTransportPolicy>(),
            sp.GetRequiredService<ExternalAuthFlowOptions>()));
        return services;
    }

    /// <summary>
    /// Adds the seam controller assembly as an MVC application part, the
    /// same mechanism the host uses for its own controllers. Call on the
    /// builder returned by AddControllers (or AddControllersAsServices).
    /// </summary>
    /// <param name="builder">The MVC builder.</param>
    /// <returns>The MVC builder.</returns>
    public static IMvcBuilder AddSlopTankExternalAuthControllers(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddApplicationPart(typeof(ExternalAuthController).Assembly);
    }
}
