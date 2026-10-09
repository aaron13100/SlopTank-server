// SlopTank modification notice: added or changed by SlopTank on 2026-10-09.

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
/// <param name="AuthenticationProviderId">The authentication provider the
/// new user is routed to (the caller's own provider id for externally
/// provisioned users); when null the server default routing is kept.</param>
/// <param name="PasswordResetProviderId">The password-reset provider routed
/// on the new user; when null the server default is used.</param>
/// <param name="Password">The secret sealed into the user's password hash
/// inside the creation transaction. Must be non-empty: an empty password is
/// exactly the passwordless default this options type exists to avoid. The
/// value is hashed through the default authentication provider's canonical
/// path and is never stored, logged or returned in clear.</param>
/// <param name="Permissions">Initial permission values applied on top of
/// the model's defaults before the transaction; targeted, caller-owned
/// fields only (the server applies exactly what is passed).</param>
/// <param name="EnabledFolders">The initial enabled-folders preference;
/// when null the model default is kept.</param>
/// <param name="UserId">An optional caller-derived stable user id. This is
/// intended for provider-neutral idempotent provisioning: the same external
/// authority identity can recover its own complete crash orphan without
/// relying on mutable display names.</param>
public sealed record ProvisionedUserCreationOptions(
    string? AuthenticationProviderId,
    string? PasswordResetProviderId,
    string Password,
    IReadOnlyDictionary<PermissionKind, bool>? Permissions,
    IReadOnlyList<Guid>? EnabledFolders,
    Guid? UserId = null);
