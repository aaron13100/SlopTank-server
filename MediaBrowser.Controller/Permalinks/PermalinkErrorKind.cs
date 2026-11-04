// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-09-19, 2026-10-14, 2026-10-31, 2026-11-04.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Classifies fail-closed permalink errors for the HTTP boundary.</summary>
public enum PermalinkErrorKind
{
    /// <summary>The item is outside the durable permalink eligibility contract.</summary>
    Ineligible,

    /// <summary>Library metadata identifies conflicting logical titles.</summary>
    Conflict,

    /// <summary>The stored alias mapping is internally inconsistent.</summary>
    Corrupt,

    /// <summary>The application database is unavailable.</summary>
    Unavailable
}
