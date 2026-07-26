using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Serializes immutable versioned permalink records into canonical UTF-8 JSON.
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes one durable document.
    /// </summary>
    public static ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    /// <summary>
    /// Deserializes one durable document and preserves its parse failure context.
    /// </summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> value, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, _options)
                ?? throw new JsonException("JSON document was null.");
        }
        catch (JsonException exception)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "malformed-document",
                $"Permalink document '{path}' is malformed ({exception.Message}).",
                exception);
        }
    }

    /// <summary>
    /// Computes a prefixed SHA-256 digest of exact canonical bytes.
    /// </summary>
    public static string Digest(ReadOnlyMemory<byte> bytes)
    {
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
    }
}

/// <summary>
/// Immutable authority identity.
/// </summary>
internal sealed record PermalinkAuthorityDocument(
    Guid AuthorityId,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-authority";

    public int Version { get; init; } = 1;
}

/// <summary>
/// Immutable never-reusable issuance tombstone.
/// </summary>
internal sealed record PermalinkIssuance(
    string Id,
    Guid IssuanceNonce,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-issuance";

    public int Version { get; init; } = 1;
}

/// <summary>
/// Immutable capsule header.
/// </summary>
internal sealed record PermalinkCapsuleDocument(
    Guid CapsuleId,
    string ItemKind,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-capsule";

    public int Version { get; init; } = 1;
}

/// <summary>
/// Immutable append-only capsule event.
/// </summary>
internal sealed record PermalinkEventDocument(
    Guid EventId,
    Guid OperationId,
    IReadOnlyList<Guid> ParentEventIds,
    string Kind,
    IReadOnlyList<PermalinkEventAlias> Ids,
    Guid? PreviousContentEventId,
    Guid? PreviousAssignmentEventId,
    IReadOnlyList<PermalinkProviderClaim> AcceptedProviderClaim,
    IReadOnlyList<string> ActiveExternalAliases,
    string ContentRoot,
    IReadOnlyList<PermalinkLeaf> Leaves,
    Guid BindingInstanceId,
    PermalinkPathAssignment PathAssignment,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-event";

    public int Version { get; init; } = 1;
}

/// <summary>
/// Alias entry persisted in an event.
/// </summary>
internal sealed record PermalinkEventAlias(
    string Id,
    Guid IssuanceNonce,
    string CreatedAt);

/// <summary>
/// Accepted external assignment entry.
/// </summary>
internal sealed record PermalinkProviderClaim(string Namespace, string Value);

/// <summary>
/// Initial binding-specific path lineage event.
/// </summary>
internal sealed record PermalinkPathAssignment(
    Guid EventId,
    Guid? PreviousPathEventId,
    string Path,
    string RootPath);

/// <summary>
/// Immutable root-local anchor election record.
/// </summary>
internal sealed record PermalinkAnchorDocument(
    string AnchorToken,
    Guid CapsuleId,
    Guid BindingInstanceId,
    Guid OperationId,
    string ItemKind,
    string ContentRoot,
    string Path,
    string RootPath,
    Guid PathEventId,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-anchor";

    public int Version { get; init; } = 1;
}

/// <summary>
/// Candidate frozen before the authority genesis election.
/// </summary>
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

/// <summary>
/// Frozen winner returned by the authority anchor election.
/// </summary>
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

/// <summary>
/// Frozen first-alias election result.
/// </summary>
internal sealed record PermalinkAliasClaim(
    string PermalinkId,
    Guid? IssuanceNonce,
    Guid EventId,
    ReadOnlyMemory<byte> EventJson,
    bool IsWinner);
