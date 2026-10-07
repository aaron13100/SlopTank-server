// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>The complete fstat identity used to prove an opened evidence file stayed unchanged.</summary>
/// <param name="ObjectIdentity">The stable device and inode.</param>
/// <param name="ContentVersion">Size, modification time and change time from the opened descriptor.</param>
public sealed record PermalinkOpenedFileIdentity(string ObjectIdentity, string ContentVersion);
