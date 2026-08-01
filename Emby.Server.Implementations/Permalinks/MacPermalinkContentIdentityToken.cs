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
    long Length);
