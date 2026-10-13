// SlopTank modification notice: added or changed by SlopTank on 2026-10-13.

using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Enums;

namespace MediaBrowser.Controller.Library;

/// <summary>Six-field, targeted policy update for an externally provisioned
/// user. The user manager rejects any permission outside this owned set and
/// fresh-loads the row under the per-user lock; unrelated local policy is
/// never copied from a stale DTO or overwritten.</summary>
/// <param name="Permissions">Targeted permission values.</param>
/// <param name="EnabledFolders">The targeted folder preference.</param>
public sealed record TargetedPolicyUpdate(
    IReadOnlyDictionary<PermissionKind, bool> Permissions,
    IReadOnlyList<Guid> EnabledFolders);
