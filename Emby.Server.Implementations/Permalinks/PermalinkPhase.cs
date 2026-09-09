// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09.
using System.Collections.Generic;
using System.Globalization;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Names one durable journal phase and binds its <see cref="PermalinkPhaseDisposition"/> at the
/// point of declaration.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way to name a phase. The constructor is private and every factory demands a
/// disposition, so a phase cannot be written that the journal is unable to classify. Terminality
/// used to be a membership test against a separate hardcoded string array, which is how
/// <c>restored</c> and <c>manual_intervention</c> came to be written by one part of the system and
/// unknown to another: they fell out of the array by omission rather than by decision.
/// </para>
/// <para>
/// <see cref="Declared"/> accumulates in declaration order and is the single list both the item
/// fence and the automatic-progress gate read, so the two can no longer disagree. Terminal phases
/// are declared before <see cref="Restored"/> so that a journal somehow holding both still reports
/// the closed outcome.
/// </para>
/// </remarks>
internal sealed class PermalinkPhase
{
    private static readonly List<PermalinkPhase> _declared = [];

    private PermalinkPhase(string name, PermalinkPhaseDisposition disposition)
    {
        Name = name;
        Disposition = disposition;
    }

    /// <summary>Gets the frozen immutable input boundary.</summary>
    public static PermalinkPhase Prepared { get; }
        = Declare("prepared", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the verified desired-evidence boundary.</summary>
    public static PermalinkPhase Ready { get; }
        = Declare("ready", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the boundary at which desired bytes became live.</summary>
    public static PermalinkPhase Published { get; }
        = Declare("published", PermalinkPhaseDisposition.Pending);

    /// <summary>
    /// Gets the recoverable post-claim mismatch. Live state was left untouched and recovery may
    /// resume once prepared-old is restored, so the item stays fenced.
    /// </summary>
    public static PermalinkPhase ClaimedPending { get; }
        = Declare("claimed_pending", PermalinkPhaseDisposition.Pending);

    /// <summary>
    /// Gets the mixed or unrecognized evidence boundary. Nothing may label arbitrary bytes as the
    /// old or the desired work, so the item stays fenced until a guarded administrator action.
    /// </summary>
    public static PermalinkPhase ManualIntervention { get; }
        = Declare("manual_intervention", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the completed mutation.</summary>
    public static PermalinkPhase Committed { get; }
        = Declare("committed", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets the mutation abandoned before it took any effect.</summary>
    public static PermalinkPhase Aborted { get; }
        = Declare("aborted", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets the mutation rolled back to its exact prepared-old assignment.</summary>
    public static PermalinkPhase Cancelled { get; }
        = Declare("cancelled", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets the unrecoverable mutation whose bindings were removed.</summary>
    public static PermalinkPhase Detached { get; }
        = Declare("detached", PermalinkPhaseDisposition.Terminal);

    /// <summary>
    /// Gets the closure for partial evidence whose exact old or desired assignment cannot be
    /// reconstructed. It closes the fence without asserting what the partial metadata means: it
    /// claims nothing, detaches nothing, removes no binding and permits no remint.
    /// </summary>
    public static PermalinkPhase AssignmentUnknown { get; }
        = Declare("assignment_unknown", PermalinkPhaseDisposition.Terminal);

    /// <summary>
    /// Gets the administrator restore of verified prepared-old bytes onto the live path.
    /// </summary>
    public static PermalinkPhase Restored { get; }
        = Declare("restored", PermalinkPhaseDisposition.Reverted);

    /// <summary>Gets every declared phase, in the order terminality is reported.</summary>
    public static IReadOnlyList<PermalinkPhase> Declared => _declared;

    /// <summary>Gets the phase name, which is also its deterministic journal filename stem.</summary>
    public string Name { get; }

    /// <summary>Gets what this phase means for item fencing and for automatic recovery.</summary>
    public PermalinkPhaseDisposition Disposition { get; }

    /// <summary>Gets a value indicating whether a journal holding this phase still fences its item.</summary>
    public bool FencesItem => Disposition == PermalinkPhaseDisposition.Pending;

    /// <summary>Returns the per-part published boundary of a multipart publication.</summary>
    /// <param name="index">The zero-based part index in immutable plan order.</param>
    /// <returns>The resulting phase.</returns>
    public static PermalinkPhase PartPublished(int index)
    {
        return Part(index, "published");
    }

    /// <summary>Returns the per-part source-quarantine boundary of a multipart publication.</summary>
    /// <param name="index">The zero-based part index in immutable plan order.</param>
    /// <returns>The resulting phase.</returns>
    public static PermalinkPhase PartQuarantined(int index)
    {
        return Part(index, "quarantined");
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }

    private static PermalinkPhase Declare(string name, PermalinkPhaseDisposition disposition)
    {
        var phase = new PermalinkPhase(name, disposition);
        _declared.Add(phase);
        return phase;
    }

    private static PermalinkPhase Part(int index, string state)
    {
        // Per-part boundaries are progress markers inside one unfinished multipart publication, so
        // they are never scanned for terminality and are deliberately not declared.
        return new PermalinkPhase(
            string.Create(CultureInfo.InvariantCulture, $"part-{index}-{state}"),
            PermalinkPhaseDisposition.Pending);
    }
}
