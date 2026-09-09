// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Folded state returned by durable storage.</summary>
/// <param name="CapsuleId">The durable capsule id.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="Aliases">The ordered active aliases.</param>
public sealed record PermalinkStoredState(
    Guid CapsuleId,
    string ContentRoot,
    IReadOnlyList<string> Aliases);
