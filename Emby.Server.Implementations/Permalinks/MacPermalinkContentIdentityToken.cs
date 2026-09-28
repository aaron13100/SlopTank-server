// SlopTank modification notice: added or changed by SlopTank on 2026-08-01, 2026-09-09, 2026-09-28.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// A filesystem-guaranteed content change token: which file (device, inode),
/// and proof it was not altered since capture (status change time, byte
/// length).
/// </summary>
/// <param name="Device">The device id containing the file.</param>
/// <param name="Inode">The file's inode number on that device.</param>
/// <param name="ChangeTimeSeconds">The whole-second part of the inode's last status change time.</param>
/// <param name="ChangeTimeNanoseconds">The sub-second part of the inode's last status change time.</param>
/// <param name="Length">The file's byte length as of the same stat call.</param>
internal readonly record struct MacPermalinkContentIdentityToken(
    int Device,
    ulong Inode,
    long ChangeTimeSeconds,
    long ChangeTimeNanoseconds,
    long Length)
{
    private const long CoarseChangeTimeSettlingSeconds = 2;

    /// <summary>
    /// Gets the earliest moment a full-content read may start and still have
    /// its digest authorized by this token. A zero nanosecond field can mean
    /// the backing file system records ctime only to whole seconds (HFS+
    /// does). A same-length rewrite within that second leaves the token
    /// unchanged, so only a read that starts after the second has closed can
    /// prove it saw the final bytes: any later write stamps a later ctime.
    /// </summary>
    public DateTimeOffset SettledAt
        => ChangeTimeNanoseconds != 0
            ? DateTimeOffset.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(ChangeTimeSeconds + CoarseChangeTimeSettlingSeconds);

    /// <summary>
    /// Returns whether this token can safely authorize a digest whose content
    /// read started at the supplied time. This is the racy case git guards in
    /// its index: a read that began inside the ctime's ambiguity window may
    /// have missed a rewrite the token cannot see.
    /// </summary>
    /// <param name="readStartedAt">When the full-content read began.</param>
    /// <returns>True when the token is precise or the read began after it settled.</returns>
    public bool CanAuthorizeDigestReadStartedAt(DateTimeOffset readStartedAt)
        => readStartedAt >= SettledAt;
}
