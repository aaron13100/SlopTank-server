// SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
namespace MediaBrowser.Controller.Library;

/// <summary>Identifies the operation that removed library records.</summary>
public enum LibraryRemovalReason
{
    /// <summary>The library removed records without an explicit deletion request.</summary>
    Library = 0,

    /// <summary>An authorized caller explicitly requested deletion.</summary>
    Requested = 1
}
