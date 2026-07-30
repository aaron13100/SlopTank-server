using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Api.Extensions;

/// <summary>
/// Extensions for <see cref="ClaimsPrincipal"/>.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Get user id from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>User id.</returns>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var value = GetClaimValue(user, InternalClaimTypes.UserId);
        return string.IsNullOrEmpty(value)
            ? default
            : Guid.Parse(value);
    }

    /// <summary>
    /// Resolves the user a request was made on behalf of, or <c>null</c> when the
    /// token carries no user at all.
    /// </summary>
    /// <remarks>
    /// An API key satisfies <c>[Authorize]</c> but authenticates the server rather
    /// than a person, so <see cref="GetUserId"/> yields <see cref="Guid.Empty"/> for
    /// it. <see cref="IUserManager.GetUserById"/> throws on an empty id rather than
    /// returning <c>null</c>, so calling it directly turns "there is no user" into an
    /// unhandled exception and an untyped error response. Endpoints that require a
    /// user must ask through here, where the absence is a value they can refuse on.
    /// </remarks>
    /// <param name="user">Current claims principal.</param>
    /// <param name="userManager">The user manager to resolve the claim against.</param>
    /// <returns>The authenticated user, or <c>null</c> when the request has none.</returns>
    public static User? GetRequestUser(this ClaimsPrincipal user, IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(userManager);

        var userId = user.GetUserId();
        return userId.IsEmpty() ? null : userManager.GetUserById(userId);
    }

    /// <summary>
    /// Get device id from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>Device id.</returns>
    public static string? GetDeviceId(this ClaimsPrincipal user)
        => GetClaimValue(user, InternalClaimTypes.DeviceId);

    /// <summary>
    /// Get device from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>Device.</returns>
    public static string? GetDevice(this ClaimsPrincipal user)
        => GetClaimValue(user, InternalClaimTypes.Device);

    /// <summary>
    /// Get client from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>Client.</returns>
    public static string? GetClient(this ClaimsPrincipal user)
        => GetClaimValue(user, InternalClaimTypes.Client);

    /// <summary>
    /// Get version from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>Version.</returns>
    public static string? GetVersion(this ClaimsPrincipal user)
        => GetClaimValue(user, InternalClaimTypes.Version);

    /// <summary>
    /// Get token from claims.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>Token.</returns>
    public static string? GetToken(this ClaimsPrincipal user)
        => GetClaimValue(user, InternalClaimTypes.Token);

    /// <summary>
    /// Gets a flag specifying whether the request is using an api key.
    /// </summary>
    /// <param name="user">Current claims principal.</param>
    /// <returns>The flag specifying whether the request is using an api key.</returns>
    public static bool GetIsApiKey(this ClaimsPrincipal user)
    {
        var claimValue = GetClaimValue(user, InternalClaimTypes.IsApiKey);
        return bool.TryParse(claimValue, out var parsedClaimValue)
               && parsedClaimValue;
    }

    private static string? GetClaimValue(in ClaimsPrincipal user, string name)
        => user.Claims.FirstOrDefault(claim => claim.Type.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
