// SlopTank modification notice: added or changed by SlopTank on 2026-09-19, 2026-10-31.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>The manifest and live-oracle fields for one authority generation.</summary>
internal sealed record PermalinkAuthorityGeneration(
    long Generation,
    string AuthorityId,
    long SchemaEpoch,
    long ContentEpoch,
    long JournalCounter,
    string CreatedAt,
    long DatabaseLength,
    string DatabaseSha256);
