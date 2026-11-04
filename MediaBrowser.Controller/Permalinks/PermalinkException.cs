// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-08-30, 2026-09-04, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-04.
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
    /// <param name="itemId">The affected library item, when known.</param>
    public PermalinkException(
        PermalinkErrorKind kind,
        string code,
        string message,
        Exception? innerException = null,
        Guid? itemId = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        ItemId = itemId;
    }

    /// <summary>Gets the failure class.</summary>
    public PermalinkErrorKind Kind { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the affected library item, when known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets a value indicating whether this failure belongs to one metadata record.</summary>
    public bool IsItemScoped => Kind is not PermalinkErrorKind.Unavailable;
}
