using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Carries a specific durable permalink failure without hiding its cause.</summary>
public sealed class PermalinkException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PermalinkException"/> class.</summary>
    /// <param name="kind">The failure class.</param>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="message">The actionable failure message.</param>
    /// <param name="innerException">The underlying failure, when present.</param>
    public PermalinkException(
        PermalinkErrorKind kind,
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>Gets the failure class.</summary>
    public PermalinkErrorKind Kind { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }
}
