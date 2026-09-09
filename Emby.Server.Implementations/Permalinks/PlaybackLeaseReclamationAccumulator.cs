// SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
namespace Emby.Server.Implementations.Permalinks;

internal sealed class PlaybackLeaseReclamationAccumulator
{
    public PlaybackLeaseReclamationAccumulator(int maximumEntriesPerPass)
    {
        MaximumEntriesPerPass = maximumEntriesPerPass;
    }

    public int MaximumEntriesPerPass { get; }

    public int Examined { get; set; }

    public int Reclaimed { get; set; }

    public int Active { get; set; }

    public int Retained { get; set; }

    public int Refused { get; set; }

    public int Failed { get; set; }

    public bool RootRefused { get; set; }

    public PlaybackLeaseReclamationResult Freeze()
    {
        return new PlaybackLeaseReclamationResult(
            true,
            Examined,
            Reclaimed,
            Active,
            Retained,
            Refused,
            Failed,
            RootRefused,
            MaximumEntriesPerPass);
    }
}
