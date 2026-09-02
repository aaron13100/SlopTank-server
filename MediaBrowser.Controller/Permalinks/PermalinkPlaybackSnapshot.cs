using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Verified playback result for one frozen queue entry.</summary>
/// <param name="ItemId">The verified item identifier.</param>
/// <param name="VerifiedPath">The verified source path for this entry.</param>
/// <param name="QueueCount">The frozen queue length.</param>
/// <param name="PlaybackSessionId">The user-bound playback session.</param>
/// <param name="VerifiedPaths">
/// The verified source paths for the current queue entry, in frozen order.
/// These name the library media the lease was issued against; playback no
/// longer copies media into the lease root, so they are deliberately not
/// snapshot paths and are renamed to keep a stale consumer failing loudly
/// rather than silently reading a different guarantee.
/// </param>
public sealed record PermalinkPlaybackSnapshot(
    Guid ItemId,
    string VerifiedPath,
    int QueueCount,
    string PlaybackSessionId,
    IReadOnlyList<string> VerifiedPaths);
