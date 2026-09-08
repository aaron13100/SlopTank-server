namespace Emby.Server.Implementations.Permalinks;

/// <summary>Summarizes one terminal-operation archive pass.</summary>
/// <param name="Examined">The hot directories examined.</param>
/// <param name="Archived">The terminal directories atomically moved.</param>
/// <param name="Retained">The terminal directories still within retention.</param>
/// <param name="Refused">The invalid, linked, pending, or suspended directories retained.</param>
/// <param name="Failed">The eligible directories retained after a safe move failure.</param>
/// <param name="MaximumOperationsPerPass">The hard examination/move ceiling for this pass.</param>
internal sealed record PermalinkOperationArchiveResult(
    int Examined,
    int Archived,
    int Retained,
    int Refused,
    int Failed,
    int MaximumOperationsPerPass);
