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
