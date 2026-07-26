using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Issues and validates durable permalink aliases for visible library items.
/// </summary>
public interface IPermalinkManager
{
    /// <summary>
    /// Ensures the item has a currently resolvable ordered alias set.
    /// </summary>
    /// <param name="item">The local library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The versioned permalink response.</returns>
    Task<PermalinkResponse> EnsurePermalinkIdsAsync(
        BaseItem item,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists and validates the durable permalink state owned outside metadata.
/// </summary>
public interface IPermalinkStore
{
    /// <summary>
    /// Ensures one verified fallback alias for the supplied snapshot.
    /// </summary>
    /// <param name="request">The immutable item snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded durable state.</returns>
    Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Versioned response returned by the permalink endpoint.
/// </summary>
/// <param name="Version">The response format version.</param>
/// <param name="Ids">The ordered active aliases.</param>
/// <param name="CanonicalId">The first canonical alias.</param>
public sealed record PermalinkResponse(
    int Version,
    IReadOnlyList<string> Ids,
    string CanonicalId);

/// <summary>
/// Immutable request passed from business orchestration to durable storage.
/// </summary>
/// <param name="ItemId">The current Jellyfin item id.</param>
/// <param name="ItemKind">The immutable permalink item kind.</param>
/// <param name="Path">The current local path.</param>
/// <param name="ContentRoot">The canonical content root.</param>
/// <param name="Leaves">The complete canonical leaf multiset.</param>
/// <param name="PublishAlias">Whether this item is the user-requested item.</param>
/// <param name="PreferredAlias">The accepted canonical provider alias, when one exists.</param>
public sealed record PermalinkStoreRequest(
    Guid ItemId,
    string ItemKind,
    string Path,
    string ContentRoot,
    IReadOnlyList<PermalinkLeaf> Leaves,
    bool PublishAlias,
    string? PreferredAlias);

/// <summary>
/// One canonical content-evidence leaf.
/// </summary>
/// <param name="Kind">The leaf kind.</param>
/// <param name="Role">The playback or membership role.</param>
/// <param name="Path">The normalized relative path, when path-sensitive.</param>
/// <param name="Digest">The SHA-256 digest.</param>
/// <param name="ByteLength">The full byte length.</param>
/// <param name="Count">The multiset count.</param>
public sealed record PermalinkLeaf(
    string Kind,
    string Role,
    string? Path,
    string Digest,
    long ByteLength,
    int Count);

/// <summary>
/// Folded state returned by durable storage.
/// </summary>
/// <param name="CapsuleId">The durable capsule id.</param>
/// <param name="ContentRoot">The verified content root.</param>
/// <param name="Aliases">The ordered active aliases.</param>
public sealed record PermalinkStoredState(
    Guid CapsuleId,
    string ContentRoot,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Classifies fail-closed permalink errors for the HTTP boundary.
/// </summary>
public enum PermalinkErrorKind
{
    /// <summary>The item is outside the durable permalink eligibility contract.</summary>
    Ineligible,

    /// <summary>Durable evidence is ambiguous, corrupt, or does not match.</summary>
    Conflict,

    /// <summary>A required content or authority root cannot be verified.</summary>
    Unavailable
}

/// <summary>
/// Carries a specific durable permalink failure without hiding its cause.
/// </summary>
public sealed class PermalinkException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkException"/> class.
    /// </summary>
    public PermalinkException(
        PermalinkErrorKind kind,
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// Gets the failure class.
    /// </summary>
    public PermalinkErrorKind Kind { get; }

    /// <summary>
    /// Gets the stable diagnostic code.
    /// </summary>
    public string Code { get; }
}
