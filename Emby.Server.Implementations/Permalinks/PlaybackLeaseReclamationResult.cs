// SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
namespace Emby.Server.Implementations.Permalinks;

internal sealed record PlaybackLeaseReclamationResult(
    bool Enabled,
    int Examined,
    int Reclaimed,
    int Active,
    int Retained,
    int Refused,
    int Failed,
    bool RootRefused,
    int MaximumEntriesPerPass)
{
    public static PlaybackLeaseReclamationResult Disabled(int maximumEntriesPerPass)
        => new(false, 0, 0, 0, 0, 0, 0, false, maximumEntriesPerPass);

    public static PlaybackLeaseReclamationResult Noop(int maximumEntriesPerPass)
        => new(true, 0, 0, 0, 0, 0, 0, false, maximumEntriesPerPass);
}
