// SlopTank modification notice: added or changed by SlopTank on 2026-10-02.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The outcome counts of one anchor-intent recovery pass.
/// </summary>
/// <param name="Examined">Pending anchor-intent rows the pass looked at.</param>
/// <param name="Abandoned">Rows moved to abandoned.</param>
/// <param name="AnchorsRemoved">Abandoned rows whose own anchor was found and removed from disk.</param>
/// <param name="LeftPending">Rows left pending because the root was unknown, the token was missing, or the row failed.</param>
internal sealed record PermalinkAnchorIntentRecoveryResult(int Examined, int Abandoned, int AnchorsRemoved, int LeftPending);
