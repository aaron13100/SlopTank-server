using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Frozen winner returned by the authority anchor election.
/// </summary>
/// <param name="AnchorToken">The stable anchor token.</param>
/// <param name="CapsuleId">The elected capsule identifier.</param>
/// <param name="BindingId">The elected binding identifier.</param>
/// <param name="RootPath">The admitted content root.</param>
/// <param name="CapsulePath">The capsule publication path.</param>
/// <param name="ItemKind">The immutable item kind.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="CurrentPath">The current media path.</param>
/// <param name="HeaderJson">The canonical capsule header.</param>
/// <param name="EventJson">The canonical genesis event.</param>
/// <param name="AnchorJson">The canonical anchor record.</param>
/// <param name="IssuedId">The optional fallback identifier.</param>
/// <param name="IssuanceNonce">The optional issuance nonce.</param>
/// <param name="CreatedAt">The canonical creation timestamp.</param>
/// <param name="IsWinner">Whether this caller won the election.</param>
internal sealed record PermalinkGenesisReservation(
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
    string? IssuedId,
    Guid? IssuanceNonce,
    string CreatedAt,
    bool IsWinner);
