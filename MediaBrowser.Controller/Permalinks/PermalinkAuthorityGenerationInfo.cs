// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Independent live freshness state for authority snapshot consumers.</summary>
public sealed record PermalinkAuthorityGenerationInfo(
    long AuthoritySequence,
    long ContentEpoch,
    long JournalCounter,
    long LatestPublishedGeneration,
    long SchemaEpoch);
