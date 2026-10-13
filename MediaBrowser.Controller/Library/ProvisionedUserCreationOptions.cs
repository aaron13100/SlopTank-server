// SlopTank modification notice: added or changed by SlopTank on 2026-10-09, 2026-10-13.

using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Provider-neutral options for creating a user that must NEVER be
/// observable in an intermediate, accessible-by-default state: the
/// authentication-provider routing, the sealed password hash and the
/// initial permission/preference values are persisted in the SAME
/// transaction as the user row, before the row is visible to any other
/// reader and before the <c>UserCreated</c> event fires. The plain
/// <see cref="IUserManager.CreateUserAsync(string)"/> overload keeps its
/// historical behavior for ordinary flows; callers that cannot tolerate a
/// passwordless crash window (provisioning from an external identity, API
/// creation with a password) must use this overload.
/// </summary>
public sealed class ProvisionedUserCreationOptions
{
    private readonly string _password;

    /// <summary>Initializes secret-safe atomic user creation options.</summary>
    /// <param name="authenticationProviderId">The new user's auth route.</param>
    /// <param name="passwordResetProviderId">The password reset route.</param>
    /// <param name="password">The non-empty plaintext seal, held only in
    /// memory until the creation path hashes it.</param>
    /// <param name="permissions">Initial targeted permission values.</param>
    /// <param name="enabledFolders">Initial enabled folders.</param>
    /// <param name="userId">Optional stable id for idempotent provisioning.</param>
    public ProvisionedUserCreationOptions(
        string? authenticationProviderId,
        string? passwordResetProviderId,
        string password,
        IReadOnlyDictionary<PermissionKind, bool>? permissions,
        IReadOnlyList<Guid>? enabledFolders,
        Guid? userId = null)
    {
        AuthenticationProviderId = authenticationProviderId;
        PasswordResetProviderId = passwordResetProviderId;
        _password = password;
        Permissions = permissions;
        EnabledFolders = enabledFolders;
        UserId = userId;
    }

    /// <summary>The new user's authentication provider route.</summary>
    public string? AuthenticationProviderId { get; }

    /// <summary>The new user's password-reset provider route.</summary>
    public string? PasswordResetProviderId { get; }

    /// <summary>Initial permission values.</summary>
    public IReadOnlyDictionary<PermissionKind, bool>? Permissions { get; }

    /// <summary>Initial enabled folders.</summary>
    public IReadOnlyList<Guid>? EnabledFolders { get; }

    /// <summary>Optional stable user identifier.</summary>
    public Guid? UserId { get; }

    /// <summary>Returns the seal only to the user creation path. No public
    /// password property exists for reflection-based serializers to expose.</summary>
    public string GetPasswordForCreation() => _password;

    /// <summary>Returns useful type and routing context without rendering
    /// the plaintext password seal in logs or exception interpolation.</summary>
    public override string ToString() =>
        $"{nameof(ProvisionedUserCreationOptions)} {{ AuthenticationProviderId = {AuthenticationProviderId}, PasswordResetProviderId = {PasswordResetProviderId}, Password = [REDACTED], UserId = {UserId} }}";
}
