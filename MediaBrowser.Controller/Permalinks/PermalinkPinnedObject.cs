// SlopTank modification notice: added or changed by SlopTank on 2026-10-02.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// The no-follow (lstat) view of the final component of a path: its pinned identity and whether
/// the component is itself a symbolic link.
/// </summary>
/// <param name="Identity">The device and inode formatted as <c>device:inode</c>.</param>
/// <param name="IsSymbolicLink">Whether the final component is a symbolic link (the link, not its target, was inspected).</param>
public sealed record PermalinkPinnedObject(string Identity, bool IsSymbolicLink);
