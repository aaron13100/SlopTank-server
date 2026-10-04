// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// Constants for the versioned playback-preparation contract.
/// </summary>
public static class PlaybackPreparationContract
{
    /// <summary>
    /// The contract discriminator accepted by both sides of the boundary.
    /// </summary>
    public const string ContractType = "sloptank.playback-preparation";

    /// <summary>
    /// The currently supported wire-contract version.
    /// </summary>
    public const int ContractVersion = 1;

    /// <summary>
    /// The currently supported compatibility-planner algorithm version.
    /// </summary>
    public const string PlannerAlgorithmVersion = "streaming-work-plan/v1";

    /// <summary>
    /// The outcome returned when the current media can be played as-is.
    /// </summary>
    public const string CompatibleOutcome = "compatible";

    /// <summary>
    /// The outcome returned when the planner has a bounded preparation plan.
    /// </summary>
    public const string NeedsPreparationOutcome = "needs_preparation";

    /// <summary>
    /// The outcome returned when the media cannot be probed reliably.
    /// </summary>
    public const string UnprobeableOutcome = "unprobeable";

    /// <summary>
    /// The outcome returned when preparation is intentionally not permitted.
    /// </summary>
    public const string RefusedOutcome = "refused";

    /// <summary>
    /// The outcome returned for an operational planner error.
    /// </summary>
    public const string ErrorOutcome = "error";
}
