// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Maps one deterministic main, additional, or tree part through publication.</summary>
/// <param name="Role">The deterministic part role.</param>
/// <param name="SourcePath">The protected source path.</param>
/// <param name="DestinationPath">The desired destination path.</param>
public sealed record PermalinkMutationPart(
    string Role,
    string SourcePath,
    string? DestinationPath);
