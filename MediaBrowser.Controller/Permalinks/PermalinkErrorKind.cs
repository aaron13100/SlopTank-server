namespace MediaBrowser.Controller.Permalinks;

/// <summary>Classifies fail-closed permalink errors for the HTTP boundary.</summary>
public enum PermalinkErrorKind
{
    /// <summary>The item is outside the durable permalink eligibility contract.</summary>
    Ineligible,

    /// <summary>Durable evidence is ambiguous, corrupt, or does not match.</summary>
    Conflict,

    /// <summary>A required content or authority root cannot be verified.</summary>
    Unavailable
}
