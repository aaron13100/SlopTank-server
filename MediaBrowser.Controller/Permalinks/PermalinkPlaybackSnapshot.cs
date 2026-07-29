using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Verified immutable playback result.</summary>
/// <param name="ItemId">The verified item identifier.</param>
/// <param name="SnapshotPath">The immutable snapshot path.</param>
/// <param name="QueueCount">The frozen queue length.</param>
/// <param name="PlaybackSessionId">The user-bound playback session.</param>
/// <param name="SnapshotPaths">The immutable paths ready for the current queue entry.</param>
public sealed record PermalinkPlaybackSnapshot(
    Guid ItemId,
    string SnapshotPath,
    int QueueCount,
    string PlaybackSessionId,
    IReadOnlyList<string> SnapshotPaths);
