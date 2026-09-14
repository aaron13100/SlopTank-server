// SlopTank modification notice: added or changed by SlopTank on 2026-09-14.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Confirms one administrator-authorized adoption of already-fenced leaf content.</summary>
/// <param name="ItemId">The fenced library item.</param>
/// <param name="RecordedContentRoot">The capsule head root the operator is replacing.</param>
/// <param name="ObservedContentRoot">The live content root the operator observed and restates.</param>
/// <param name="Confirmation">The exact adopt-content:{itemId:D}:{ObservedContentRoot} phrase.</param>
/// <param name="AdministratorUserId">The elevated caller, stamped by the server from the authenticated user.</param>
public sealed record PermalinkContentAdoptionRequest(
    Guid ItemId,
    string RecordedContentRoot,
    string ObservedContentRoot,
    string Confirmation,
    Guid? AdministratorUserId = null);
