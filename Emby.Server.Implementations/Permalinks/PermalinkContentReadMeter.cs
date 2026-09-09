// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09.
using System.Threading;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Counts full media content reads performed by the permalink subsystem.
/// </summary>
/// <remarks>
/// Opening a watch link must cost the same for a 300 MB episode and a 20 GB
/// film: identity is proved by stat, never by reading bytes. That invariant
/// used to be violated silently, because the work it cost produced an artifact
/// nothing consumed, so no behaviour changed and no test noticed. Timing cannot
/// police it either, since test hardware is roughly two orders of magnitude
/// faster than the storage the media actually lives on, and a reintroduced read
/// disappears into the noise. This meter makes the invariant directly
/// observable: a test can assert that redeeming a playback lease increments it
/// zero times, which fails deterministically the moment a content read returns
/// to that path.
/// </remarks>
public sealed class PermalinkContentReadMeter
{
    private long _fullContentReads;

    /// <summary>
    /// Gets the number of full media content reads recorded since startup.
    /// </summary>
    public long FullContentReads => Interlocked.Read(ref _fullContentReads);

    /// <summary>
    /// Records that a caller is about to read a media file end to end.
    /// </summary>
    public void RecordFullContentRead()
    {
        Interlocked.Increment(ref _fullContentReads);
    }
}
