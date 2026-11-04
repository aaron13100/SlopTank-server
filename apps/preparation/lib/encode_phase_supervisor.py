# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Stateful session wiring a live observation into the pure phase-recovery policy.

encode_phase_recovery.py owns the pure decision (no I/O, no memory of prior
calls). This module owns the OTHER half: pinning one encode's expected
identity, remembering its phase across repeated polls, and feeding whatever
an injected observer returns into that pure policy -- fail-closed the moment
the observer raises, returns garbage, or setup itself fails.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Callable

from lib.encode_phase_recovery import (
    ACTION_REFUSE,
    PHASE_HARDWARE_ENCODE,
    RESTART_SAFETY_MARGIN_SECONDS,
    DecisionContext,
    PhaseTelemetry,
    RecoveryDecision,
    _refusal,
    decide_phase_recovery,
)


class PhaseRecoverySupervisor:
    """Pin process identity and feed injected observations to the pure policy."""

    def __init__(
        self,
        *,
        expected_pid: int,
        expected_process_identity: str,
        observer: Callable[[], PhaseTelemetry],
        clock: Callable[[], float],
        max_telemetry_age_seconds: float,
        unclamped_bytes_per_second: float | None = None,
        restart_safety_margin_seconds: float = RESTART_SAFETY_MARGIN_SECONDS,
        restart_enabled: bool = True,
    ) -> None:
        self.expected_pid = expected_pid
        self.expected_process_identity = expected_process_identity
        self._observer = observer
        self._clock = clock
        self.max_telemetry_age_seconds = max_telemetry_age_seconds
        self._unclamped_bytes_per_second = unclamped_bytes_per_second
        self._restart_safety_margin_seconds = restart_safety_margin_seconds
        self._restart_enabled = restart_enabled
        self._phase = PHASE_HARDWARE_ENCODE

    def evaluate(
        self, *, clamped: bool, prior_latch: str, attendance_reason: str,
    ) -> RecoveryDecision:
        """Evaluate one injected sample; probe failures can only retain a clamp."""
        try:
            observed = self._observer()
        except Exception as error:  # noqa: BLE001 - observation is fail-closed
            return _refusal(
                prior_latch,
                f"phase observer failed ({type(error).__name__})",
            )
        if not isinstance(observed, PhaseTelemetry):
            return _refusal(prior_latch, "phase observer returned invalid telemetry")
        sample = replace(observed, attendance_reason=attendance_reason)
        decision = decide_phase_recovery(
            DecisionContext(
                expected_pid=self.expected_pid,
                expected_process_identity=self.expected_process_identity,
                prior_phase=self._phase,
                now=self._clock(),
                max_telemetry_age_seconds=self.max_telemetry_age_seconds,
            ),
            sample,
            clamped=clamped,
            prior_latch=prior_latch,
            unclamped_bytes_per_second=self._unclamped_bytes_per_second,
            restart_safety_margin_seconds=self._restart_safety_margin_seconds,
            restart_enabled=self._restart_enabled,
        )
        if decision.action != ACTION_REFUSE:
            self._phase = decision.phase
        return decision


def build_injected_supervisor(
    factory, pid: int, command: tuple[str, ...], progress_path,
    *, uses_hardware: bool, logger, restart_enabled: bool = True,
):
    """Construct the optional supervisor; setup failure keeps hardware one-way."""
    if factory is None or progress_path is None or not uses_hardware:
        return None
    try:
        return factory(pid, command, progress_path, restart_enabled=restart_enabled)
    except Exception as error:  # noqa: BLE001 - optional throughput seam is fail-closed
        logger.warning(
            "phase recovery setup failed (%s: %s); hardware clamp stays one-way",
            type(error).__name__, error)
        return None
