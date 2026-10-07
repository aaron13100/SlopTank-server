// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09, 2026-10-07.
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

    /// <summary>Gets the orphan-closure recoverability boundary.</summary>
    public static PermalinkPhase ClosureReady { get; }
        = Declare("closure_ready", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the durable detached/no-change authority decision.</summary>
    public static PermalinkPhase DetachedDecided { get; }
        = Declare("detached_decided", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the binding-removal boundary.</summary>
    public static PermalinkPhase BindingsRemoved { get; }
        = Declare("bindings_removed", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the exact old-byte staging boundary.</summary>
    public static PermalinkPhase OldStaged { get; }
        = Declare("old_staged", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the durable restoration authorization boundary.</summary>
    public static PermalinkPhase RestorationAuthorized { get; }
        = Declare("restoration_authorized", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the exact old-byte installation boundary.</summary>
    public static PermalinkPhase OldInstalled { get; }
        = Declare("old_installed", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the old-byte equality verification boundary.</summary>
    public static PermalinkPhase EqualityVerified { get; }
        = Declare("equality_verified", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the desired rewrite staging boundary.</summary>
    public static PermalinkPhase RewritePrepared { get; }
        = Declare("rewrite_prepared", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the desired-evidence boundary for a composite rewrite.</summary>
    public static PermalinkPhase DesiredReady { get; }
        = Declare("desired_ready", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the irreversible predecessor-claim boundary.</summary>
    public static PermalinkPhase ClaimedReady { get; }
        = Declare("claimed_ready", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the desired-byte installation boundary.</summary>
    public static PermalinkPhase DesiredInstalled { get; }
        = Declare("desired_installed", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the content-successor publication boundary.</summary>
    public static PermalinkPhase ContentPublished { get; }
        = Declare("content_published", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the aggregate-successor publication boundary.</summary>
    public static PermalinkPhase AggregatesPublished { get; }
        = Declare("aggregates_published", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the path-successor publication boundary.</summary>
    public static PermalinkPhase PathPublished { get; }
        = Declare("path_published", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the successor candidate verification boundary.</summary>
    public static PermalinkPhase DesiredVerified { get; }
        = Declare("desired_verified", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the successor anchor-write boundary.</summary>
    public static PermalinkPhase XattrSet { get; }
        = Declare("xattr_set", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the durable supersession authorization boundary.</summary>
    public static PermalinkPhase SupersessionAuthorized { get; }
        = Declare("supersession_authorized", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the successor capsule publication boundary.</summary>
    public static PermalinkPhase CapsulePublished { get; }
        = Declare("capsule_published", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the successor binding publication boundary.</summary>
    public static PermalinkPhase Bound { get; }
        = Declare("bound", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the pending-slot publication boundary.</summary>
    public static PermalinkPhase SlotPublished { get; }
        = Declare("slot_published", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the scratch desired-copy boundary.</summary>
    public static PermalinkPhase DesiredCopied { get; }
        = Declare("desired_copied", PermalinkPhaseDisposition.Pending);

    /// <summary>Gets the scratch registry-transfer boundary.</summary>
    public static PermalinkPhase RegistryTransferred { get; }
        = Declare("registry_transferred", PermalinkPhaseDisposition.Pending);

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

    /// <summary>Gets an exact restoration terminal.</summary>
    public static PermalinkPhase TerminalRestored { get; }
        = Declare("terminal_restored", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets an exact restoration handed to its composite rewrite.</summary>
    public static PermalinkPhase HandedToRewrite { get; }
        = Declare("handed_to_rewrite", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets a detached operation whose explicit future successor slot is durable.</summary>
    public static PermalinkPhase DetachedPending { get; }
        = Declare("detached_pending", PermalinkPhaseDisposition.Terminal);

    /// <summary>Gets a completed scratch adoption.</summary>
    public static PermalinkPhase Adopted { get; }
        = Declare("adopted", PermalinkPhaseDisposition.Terminal);

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
