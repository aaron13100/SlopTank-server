using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Represents a verified derived alias-to-item binding.
/// </summary>
/// <param name="PermalinkId">The bound permalink identifier.</param>
/// <param name="ItemId">The current Jellyfin item identifier.</param>
/// <param name="CapsuleId">The logical capsule identifier.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="AnchorToken">The stable physical anchor.</param>
/// <param name="BindingInstanceId">The elected physical binding instance.</param>
/// <param name="CurrentPath">The currently bound media path.</param>
/// <param name="IsCapsuleOverride">Whether recovery state overrides the derived binding.</param>
internal sealed record PermalinkResolutionBinding(
    string PermalinkId,
    Guid ItemId,
    Guid CapsuleId,
    string ContentRoot,
    string AnchorToken,
    Guid BindingInstanceId,
    string CurrentPath,
    bool IsCapsuleOverride);
