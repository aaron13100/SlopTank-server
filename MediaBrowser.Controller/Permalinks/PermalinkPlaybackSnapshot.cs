using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Verified immutable playback result.</summary>
/// <param name="ItemId">The verified item identifier.</param>
/// <param name="SnapshotPath">The immutable snapshot path.</param>
/// <param name="QueueCount">The frozen queue length.</param>
public sealed record PermalinkPlaybackSnapshot(Guid ItemId, string SnapshotPath, int QueueCount);
