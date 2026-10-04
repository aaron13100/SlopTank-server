// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using Microsoft.AspNetCore.Authorization;

namespace Jellyfin.Api.Auth.PlaybackPreparationWorkerPolicy;

/// <summary>Requires the dedicated loopback worker credential.</summary>
public sealed class PlaybackPreparationWorkerRequirement : IAuthorizationRequirement;
