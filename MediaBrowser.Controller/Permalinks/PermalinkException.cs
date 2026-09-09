// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-08-30, 2026-09-04, 2026-09-09.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Carries a specific durable permalink failure without hiding its cause.</summary>
public sealed class PermalinkException : Exception
{
    /// <summary>
    /// The failures that are about the permalink authority rather than about
    /// the one item being processed.
    ///
    /// Everything else is item scoped, which is the safe default: a caller
    /// sweeping many items can skip a failed one and keep going, and a new
    /// per-item failure code therefore behaves correctly without anyone having
    /// to remember this list. These three cannot be skipped that way, because
    /// they will fail identically for every item, and a sweep that quietly
    /// continued would mint nothing across the whole estate while reporting
    /// success.
    /// </summary>
    private static readonly HashSet<string> _authorityScopedCodes = new(StringComparer.Ordinal)
    {
        "authority-not-configured",
        "authority-unavailable",
        "allocation-exhausted",
    };

    /// <summary>Initializes a new instance of the <see cref="PermalinkException"/> class.</summary>
    /// <param name="kind">The failure class.</param>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="message">The actionable failure message.</param>
    /// <param name="innerException">The underlying failure, when present.</param>
    /// <param name="operationId">The durable operation that caused the failure, when known.</param>
    /// <param name="itemId">The affected library item, when known.</param>
    public PermalinkException(
        PermalinkErrorKind kind,
        string code,
        string message,
        Exception? innerException = null,
        Guid? operationId = null,
        Guid? itemId = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        OperationId = operationId;
        ItemId = itemId;
    }

    /// <summary>Gets the failure class.</summary>
    public PermalinkErrorKind Kind { get; }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the durable operation that caused the failure, when known.</summary>
    public Guid? OperationId { get; }

    /// <summary>Gets the affected library item, when known.</summary>
    public Guid? ItemId { get; }

    /// <summary>
    /// Gets a value indicating whether this failure is attributable to one item.
    /// </summary>
    /// <remarks>
    /// A caller processing many items uses this to decide whether skipping the
    /// failed one and continuing is honest. It is deliberately independent of
    /// <see cref="Kind"/>: all three kinds carry item-scoped codes, so a filter
    /// written on the kind alone would either abort a whole library scan
    /// because one file went missing, or swallow a dead authority.
    ///
    /// It is equally independent of <see cref="OperationId"/> and
    /// <see cref="ItemId"/>. Those are optional diagnostics that most throw
    /// sites do not set, and requiring them is exactly how the library scan's
    /// own guard came to never fire.
    /// </remarks>
    /// <remarks>
    /// The null check is not decoration: this property is evaluated inside a
    /// catch filter, and C# swallows an exception thrown in a filter and reads
    /// it as no-match. A HashSet lookup on a null key throws, so a throw site
    /// that passed a null code would silently restore the old behaviour of
    /// aborting the whole sweep, and nothing would say why.
    /// </remarks>
    public bool IsItemScoped => Code is null || !_authorityScopedCodes.Contains(Code);
}
