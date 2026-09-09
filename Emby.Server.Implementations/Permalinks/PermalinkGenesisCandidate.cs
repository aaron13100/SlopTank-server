// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Candidate frozen before the authority genesis election.
/// </summary>
/// <param name="AnchorToken">The stable anchor token.</param>
/// <param name="CapsuleId">The proposed capsule identifier.</param>
/// <param name="BindingId">The proposed binding identifier.</param>
/// <param name="RootPath">The admitted content root.</param>
/// <param name="CapsulePath">The capsule publication path.</param>
/// <param name="ItemKind">The immutable item kind.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="CurrentPath">The current media path.</param>
/// <param name="HeaderJson">The canonical capsule header.</param>
/// <param name="EventJson">The canonical genesis event.</param>
/// <param name="AnchorJson">The canonical anchor record.</param>
/// <param name="Event">The parsed genesis event.</param>
/// <param name="Issuance">The optional fallback issuance.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
internal sealed record PermalinkGenesisCandidate(
    string AnchorToken,
    Guid CapsuleId,
    Guid BindingId,
    string RootPath,
    string CapsulePath,
    string ItemKind,
    string ContentRoot,
    string CurrentPath,
    ReadOnlyMemory<byte> HeaderJson,
    ReadOnlyMemory<byte> EventJson,
    ReadOnlyMemory<byte> AnchorJson,
    PermalinkEventDocument Event,
    PermalinkIssuance? Issuance,
    string CreatedAt);
