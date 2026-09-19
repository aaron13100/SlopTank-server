// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-09-19.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Classifies fail-closed permalink errors for the HTTP boundary.</summary>
public enum PermalinkErrorKind
{
    /// <summary>The item is outside the durable permalink eligibility contract.</summary>
    Ineligible,

    /// <summary>Durable evidence is ambiguous, corrupt, or does not match.</summary>
    Conflict,

    /// <summary>Durable authority state is internally inconsistent.</summary>
    Corrupt,

    /// <summary>The durable authority protocol is newer than this binary.</summary>
    VersionRefused,

    /// <summary>A required content or authority root cannot be verified.</summary>
    Unavailable
}
