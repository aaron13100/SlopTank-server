// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Classifies what one durable journal phase means for item fencing and for automatic recovery.
/// </summary>
/// <remarks>
/// Every phase carries exactly one disposition, chosen where the phase is declared, so no phase can
/// reach the journal that the fence and the automatic-progress gate do not already classify.
/// </remarks>
internal enum PermalinkPhaseDisposition
{
    /// <summary>
    /// The operation still has unfinished work: its item stays fenced and automatic recovery may
    /// advance it. The phases the design document calls suspended, <c>claimed_pending</c> and
    /// <c>manual_intervention</c>, are pending. They fence, and recovery keeps re-deriving them
    /// from evidence until an administrator supplies a guarded continuation.
    /// </summary>
    Pending,

    /// <summary>
    /// An administrator reverted the mutation to its verified prepared-old state. The item is
    /// released, because its live bytes are exactly the bytes its binding already records, while
    /// the operation's transition claim stays reserved. Automatic recovery must therefore never
    /// advance the operation, and only an administrator continuation can finish it.
    /// </summary>
    Reverted,

    /// <summary>
    /// The operation is closed. Its item is released and no further work of any kind is possible.
    /// </summary>
    Terminal
}
