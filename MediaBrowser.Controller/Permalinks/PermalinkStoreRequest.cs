// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Immutable request passed from orchestration to durable storage.</summary>
/// <param name="ItemId">The current Jellyfin item id.</param>
/// <param name="ItemKind">The immutable permalink item kind.</param>
/// <param name="Path">The current local path.</param>
/// <param name="ContentRoot">The canonical content root.</param>
/// <param name="Leaves">The complete canonical leaf multiset.</param>
/// <param name="PublishAlias">Whether this item is user-requested.</param>
/// <param name="PreferredAlias">The accepted provider alias, when one exists.</param>
public sealed record PermalinkStoreRequest(
    Guid ItemId,
    string ItemKind,
    string Path,
    string ContentRoot,
    IReadOnlyList<PermalinkLeaf> Leaves,
    bool PublishAlias,
    string? PreferredAlias);
