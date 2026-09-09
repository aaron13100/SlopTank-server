// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>One canonical content-evidence leaf.</summary>
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
