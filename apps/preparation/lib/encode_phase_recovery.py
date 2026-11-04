# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Pure, measurement-gated decisions for phase-aware encode clamp recovery.

Never signals a process, never remembers anything between calls -- see
encode_phase_supervisor.py for the stateful session that pins one encode's
identity and phase across polls. Distinguishes the VideoToolbox encode phase
(clamp stays one-way) from a verified copy/faststart tail (clamp becomes
releasable) and, for hardware, whether restarting unclamped beats riding out
the clamp -- which needs a measured unclamped rate for this box (see
organize_media_config.HARDWARE_ENCODE_UNCLAMPED_BYTES_PER_SECOND); without
one, restart stays unavailable, like every other uncertain input here.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
import math


ATTENDANCE_UNATTENDED = "confirmed-unattended"
_ATTENDANCE_REQUIRES_CLAMP = {
    "active-jellyfin-playback",
    "recent-hid-activity",
    "unknown-or-probe-failure",
}

PHASE_UNKNOWN = "unknown"
PHASE_HARDWARE_ENCODE = "hardware-encode"
PHASE_FASTSTART_TAIL = "faststart-copy-tail"
PHASE_COPY_ONLY = "copy-only"

LATCH_HARDWARE_ONE_WAY = "one-way-hardware"
LATCH_RELEASABLE_COPY = "releasable-copy"
LATCH_NONE = "none"

ACTION_REFUSE = "refuse"
ACTION_KEEP_CLAMP = "keep-clamp"
ACTION_RELEASE_CLAMP = "release-clamp"
ACTION_NO_CHANGE = "no-change"
ACTION_RESTART_ENCODE = "restart-encode"

MEASUREMENT_REQUIREMENTS = (
    "solo_unclamped_rate",
    "solo_clamped_rate",
    "two_worker_per_stream_and_aggregate_rate",
    "latch_time_distribution",
    "remaining_work_estimate",
    "stable_confirmed_unattended_window",
)

#: Default buffer (seconds) added to a full unclamped rerun's estimated time
#: before it is allowed to beat the clamped-remaining estimate. Overridable
#: per call so tests can use a smaller number; production reads
#: organize_media_config.HARDWARE_RESTART_SAFETY_MARGIN_SECONDS instead.
RESTART_SAFETY_MARGIN_SECONDS = 300.0


@dataclass(frozen=True, slots=True)
class DecisionContext:
    """Identity, prior state, and freshness boundary supplied by a supervisor."""

    expected_pid: int
    expected_process_identity: str
    prior_phase: str
    now: float
    max_telemetry_age_seconds: float


@dataclass(frozen=True, slots=True)
class PhaseTelemetry:
    """One bounded observation; no field triggers a probe from this module."""

    pid: int
    process_identity: str
    observed_at: float
    process_running: bool | None
    command_uses_hardware: bool | None
    previous_output_size: int | None
    output_size: int | None
    previous_output_mtime_ns: int | None
    output_mtime_ns: int | None
    reads_own_output: bool | None
    attendance_reason: str | None
    expected_output_bytes: int | None = None


@dataclass(frozen=True, slots=True)
class RecoveryDecision:
    """A recommendation only; restart is deliberately unavailable."""

    phase: str
    action: str
    next_latch: str
    reason: str
    restart_allowed: bool = False
    restart_threshold: float | None = None
    policy_recommendation: str = "measurement-gated"
    measurement_requirements: tuple[str, ...] = MEASUREMENT_REQUIREMENTS

    def telemetry(self) -> dict[str, object]:
        """Return stable structured output for logs or an offline analyzer."""
        return asdict(self)


def _invalid_observation(context: DecisionContext, sample: PhaseTelemetry) -> str | None:
    if context.expected_pid <= 0 or not context.expected_process_identity:
        return "invalid expected process identity"
    if sample.pid != context.expected_pid or sample.pid <= 0:
        return "pid mismatch"
    if sample.process_identity != context.expected_process_identity:
        return "process replacement detected"
    if sample.process_running is not True:
        return "process state is absent, stopped, or unknown"
    if not math.isfinite(context.now) or not math.isfinite(sample.observed_at):
        return "telemetry time is invalid"
    age = context.now - sample.observed_at
    if (
        not math.isfinite(context.max_telemetry_age_seconds)
        or context.max_telemetry_age_seconds <= 0
        or age < 0
        or age > context.max_telemetry_age_seconds
    ):
        return "telemetry is stale or from the future"
    values = (
        sample.previous_output_size,
        sample.output_size,
        sample.previous_output_mtime_ns,
        sample.output_mtime_ns,
    )
    if any(isinstance(value, bool) or not isinstance(value, int) or value < 0 for value in values):
        return "output progress telemetry is missing or invalid"
    if not isinstance(sample.command_uses_hardware, bool):
        return "hardware-session telemetry is unknown"
    if not isinstance(sample.reads_own_output, bool):
        return "process descriptor telemetry is unknown"
    return None


def _classify(sample: PhaseTelemetry) -> str:
    size_changed = sample.output_size != sample.previous_output_size
    mtime_advanced = sample.output_mtime_ns > sample.previous_output_mtime_ns
    mtime_regressed = sample.output_mtime_ns < sample.previous_output_mtime_ns
    if mtime_regressed:
        return PHASE_UNKNOWN
    if (
        sample.command_uses_hardware
        and sample.output_size
        and not size_changed
        and mtime_advanced
        and sample.reads_own_output
    ):
        return PHASE_FASTSTART_TAIL
    if sample.command_uses_hardware and size_changed:
        return PHASE_HARDWARE_ENCODE
    if not sample.command_uses_hardware and (size_changed or mtime_advanced):
        return PHASE_COPY_ONLY
    return PHASE_UNKNOWN


def _transition_allowed(prior: str, current: str) -> bool:
    allowed = {
        PHASE_UNKNOWN: {PHASE_HARDWARE_ENCODE, PHASE_COPY_ONLY},
        PHASE_HARDWARE_ENCODE: {PHASE_HARDWARE_ENCODE, PHASE_FASTSTART_TAIL},
        PHASE_COPY_ONLY: {PHASE_COPY_ONLY},
        PHASE_FASTSTART_TAIL: {PHASE_FASTSTART_TAIL},
    }
    return current in allowed.get(prior, set())


def _refusal(prior_latch: str, reason: str) -> RecoveryDecision:
    return RecoveryDecision(
        phase=PHASE_UNKNOWN,
        action=ACTION_REFUSE,
        next_latch=prior_latch,
        reason=reason,
    )


def _current_clamped_bytes_per_second(sample: PhaseTelemetry) -> float | None:
    """Live clamped throughput from two consecutive samples; None if unusable.

    Uses the SAME previous/current size and mtime pair the phase classifier
    already reads, rather than a fixed historical constant, so the estimate
    reflects whatever contention is happening right now (a second job on the
    disk, a concurrent copy) instead of a number measured under different
    conditions.
    """
    if (
        sample.previous_output_size is None or sample.output_size is None
        or sample.previous_output_mtime_ns is None or sample.output_mtime_ns is None
    ):
        return None
    byte_delta = sample.output_size - sample.previous_output_size
    time_delta_seconds = (
        sample.output_mtime_ns - sample.previous_output_mtime_ns) / 1_000_000_000
    if time_delta_seconds <= 0 or byte_delta <= 0:
        return None
    return byte_delta / time_delta_seconds


def _restart_worthwhile(
    *, output_size: int | None, expected_output_bytes: int | None,
    clamped_bytes_per_second: float | None, unclamped_bytes_per_second: float | None,
    safety_margin_seconds: float,
) -> tuple[bool, float | None]:
    """Pure remaining-time comparison; never touches a process or the clock.

    Restarting throws away this run's progress and re-encodes the whole file
    unclamped, so it is worthwhile only when finishing the REST of the file at
    the current clamped rate would take longer than redoing the WHOLE file
    unclamped, plus a fixed safety margin. Any missing or non-positive input
    makes the comparison unavailable rather than guessed, and unavailable
    always means "do not restart" -- the same fail-closed rule as everywhere
    else in this module.

    :returns: ``(worthwhile, restart_threshold_seconds)``. ``restart_threshold``
        is the break-even "redo unclamped" time this decision compared
        against, published for telemetry; it is ``None`` exactly when
        ``worthwhile`` is ``False`` because an input was unusable.
    """
    if (
        expected_output_bytes is None or expected_output_bytes <= 0
        or clamped_bytes_per_second is None or clamped_bytes_per_second <= 0
        or unclamped_bytes_per_second is None or unclamped_bytes_per_second <= 0
        or output_size is None or output_size < 0
        or not math.isfinite(safety_margin_seconds) or safety_margin_seconds < 0
    ):
        return False, None
    remaining_bytes = max(expected_output_bytes - output_size, 0)
    remaining_time_if_stay = remaining_bytes / clamped_bytes_per_second
    remaining_time_if_restart = (
        expected_output_bytes / unclamped_bytes_per_second + safety_margin_seconds)
    return remaining_time_if_stay > remaining_time_if_restart, remaining_time_if_restart


def _hardware_decision(
    sample: PhaseTelemetry, clamped: bool, *,
    unclamped_bytes_per_second: float | None,
    restart_safety_margin_seconds: float,
    restart_enabled: bool,
) -> RecoveryDecision:
    attendance_requires_clamp = (
        sample.attendance_reason in _ATTENDANCE_REQUIRES_CLAMP
        or sample.attendance_reason is None
    )
    confirmed_unattended = sample.attendance_reason == ATTENDANCE_UNATTENDED
    keep_clamped = clamped or attendance_requires_clamp
    if restart_enabled and clamped and confirmed_unattended:
        worthwhile, threshold = _restart_worthwhile(
            output_size=sample.output_size,
            expected_output_bytes=sample.expected_output_bytes,
            clamped_bytes_per_second=_current_clamped_bytes_per_second(sample),
            unclamped_bytes_per_second=unclamped_bytes_per_second,
            safety_margin_seconds=restart_safety_margin_seconds,
        )
        if worthwhile:
            return RecoveryDecision(
                phase=PHASE_HARDWARE_ENCODE,
                action=ACTION_RESTART_ENCODE,
                next_latch=LATCH_HARDWARE_ONE_WAY,
                reason=(
                    "clamped early enough that a fresh unclamped process is "
                    "expected to finish sooner than the current clamped run"
                ),
                restart_allowed=True,
                restart_threshold=threshold,
                policy_recommendation="restart-when-clamped-remaining-exceeds-fresh-unclamped-run",
            )
    reason = (
        "hardware restart remains disabled pending controlled measurements"
        if not restart_enabled
        else "clamped remaining time does not yet exceed a fresh unclamped run"
        if clamped
        else "hardware restart only applies to a currently clamped encode"
    )
    return RecoveryDecision(
        phase=PHASE_HARDWARE_ENCODE,
        action=ACTION_KEEP_CLAMP if keep_clamped else ACTION_NO_CHANGE,
        next_latch=LATCH_HARDWARE_ONE_WAY if keep_clamped else LATCH_NONE,
        reason=reason,
    )


def _copy_decision(
    phase: str,
    sample: PhaseTelemetry,
    clamped: bool,
    prior_latch: str,
) -> RecoveryDecision:
    if sample.attendance_reason == ATTENDANCE_UNATTENDED:
        action = ACTION_RELEASE_CLAMP if clamped else ACTION_NO_CHANGE
        reason = "verified copy progress can recover immediately without process restart"
    elif sample.attendance_reason in _ATTENDANCE_REQUIRES_CLAMP or sample.attendance_reason is None:
        action = ACTION_KEEP_CLAMP if clamped else ACTION_NO_CHANGE
        reason = "attendance is active or uncertain; playback protection wins"
    else:
        return _refusal(prior_latch, "unknown attendance reason")
    return RecoveryDecision(
        phase=phase,
        action=action,
        next_latch=LATCH_RELEASABLE_COPY if clamped else LATCH_NONE,
        reason=reason,
        policy_recommendation="release-copy-clamp-when-confirmed-unattended",
    )


def decide_phase_recovery(
    context: DecisionContext,
    sample: PhaseTelemetry,
    *,
    clamped: bool,
    prior_latch: str,
    unclamped_bytes_per_second: float | None = None,
    restart_safety_margin_seconds: float = RESTART_SAFETY_MARGIN_SECONDS,
    restart_enabled: bool = True,
) -> RecoveryDecision:
    """Classify one sample and recommend only a safe clamp-state transition.

    ``unclamped_bytes_per_second`` is the one input this module cannot derive
    from the sample itself: a box-level measured constant (see
    ``organize_media_config.HARDWARE_ENCODE_UNCLAMPED_BYTES_PER_SECOND``).
    Leaving it ``None`` (the default) makes every hardware-restart comparison
    unavailable, which is the same "no measurement, no restart" fallback the
    rest of this module already uses everywhere else.
    """
    if prior_latch not in {LATCH_NONE, LATCH_HARDWARE_ONE_WAY, LATCH_RELEASABLE_COPY}:
        return _refusal(prior_latch, "unknown prior clamp semantics")
    if not isinstance(clamped, bool):
        return _refusal(prior_latch, "clamp state is unknown")
    if (clamped and prior_latch == LATCH_NONE) or (not clamped and prior_latch != LATCH_NONE):
        return _refusal(prior_latch, "clamp state and semantics disagree")
    invalid = _invalid_observation(context, sample)
    if invalid:
        return _refusal(prior_latch, invalid)
    phase = _classify(sample)
    if phase == PHASE_UNKNOWN:
        return _refusal(prior_latch, "phase evidence is incomplete or contradictory")
    if not _transition_allowed(context.prior_phase, phase):
        return _refusal(prior_latch, "phase transition would move backward or cross identities")

    if phase == PHASE_HARDWARE_ENCODE:
        return _hardware_decision(
            sample, clamped,
            unclamped_bytes_per_second=unclamped_bytes_per_second,
            restart_safety_margin_seconds=restart_safety_margin_seconds,
            restart_enabled=restart_enabled,
        )
    return _copy_decision(phase, sample, clamped, prior_latch)
