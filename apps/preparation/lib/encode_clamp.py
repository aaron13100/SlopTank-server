# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""One encode's attendance-aware clamp/release/restart state machine.

See encode_clamp_registry.py for the cross-process ledger this guard reads
and writes so later launches (and its own releases) can yield to siblings.
"""

from __future__ import annotations

import logging
import threading
import time
from typing import Any, Callable, Sequence

from lib.encode_attendance import (
    ATTENDANCE_UNATTENDED,
    ATTENDANCE_UNKNOWN,
    PlaybackCheck,
    _attendance_reason,
    _default_runner,
    resolve_tool,
)
from lib.encode_clamp_registry import (
    _forget_clamped,
    _register_clamped,
    stuck_encode_clamped,
)
from lib.encode_phase_recovery import (
    ACTION_RELEASE_CLAMP,
    ACTION_RESTART_ENCODE,
    LATCH_RELEASABLE_COPY,
    LATCH_HARDWARE_ONE_WAY,
    PHASE_FASTSTART_TAIL,
    PHASE_HARDWARE_ENCODE,
)
from lib.encode_watchdog import _default_killer


DEFAULT_POLL_SECONDS = 10


class EncodeClampGuard:
    """Clamps a running encode as soon as anyone needs the host. Never unclamps.

    See the module docstring for why this is one-way: lifting the clamp after
    exec does not give the hardware encoder its speed back, so the only useful
    runtime transition is unclamped -> clamped.

    :param pid: PID of the encode. ``taskpolicy`` and ``nice`` exec in place
        rather than forking, so a Popen pid is ffmpeg's own pid.
    :param is_attended: Returns True (someone needs the host), False (nobody
        does) or None (unknown, treated as True).
    :param launched_clamped: Whether the encode was already started under the
        clamp, in which case this guard has nothing left to do.
    :param poll_seconds: Delay between polls when run via :meth:`start`.
    :param runner: Callable taking an argv and returning success, injected for tests.
    :param log: Logger for the transition.
    :param clock: Monotonic time source, injected for telemetry tests.
    """

    def __init__(self, pid: int, is_attended: PlaybackCheck, *,
                 launched_clamped: bool,
                 poll_seconds: int = DEFAULT_POLL_SECONDS,
                 runner: Callable[[Sequence[str]], bool] | None = None,
                 log: logging.Logger | None = None,
                 taskpolicy_bin: str | None = None,
                 releasable: bool = False,
                 is_running: Callable[[], bool] | None = None,
                 clock: Callable[[], float] | None = None,
                 phase_recovery: Any | None = None,
                 killer: Callable[[int], None] | None = None):
        self.pid = pid
        self.is_attended = is_attended
        self.poll_seconds = poll_seconds
        self._runner = runner or _default_runner
        self._log = log or logging.getLogger("encode_priority")
        self._bin = taskpolicy_bin or resolve_tool("taskpolicy")
        self._is_running = is_running or (lambda: True)
        self._releasable = releasable
        self._clock = clock or time.monotonic
        self._phase_recovery = phase_recovery
        self._killer = killer or _default_killer
        self._restarted = False
        self._started_at = self._clock()
        self._clamped = launched_clamped
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        if launched_clamped:
            _register_clamped(pid, self._is_running, releasable=releasable)
            self._log.info(
                "encode clamp latched at launch (reason=%s) for pid %d "
                "(latched_after_seconds=%.3f)",
                _attendance_reason(self.is_attended), self.pid, 0.0)

    @property
    def clamped(self) -> bool:
        """Whether the encode is currently believed to be in PRIO_DARWIN_BG."""
        return self._clamped

    def _clamp(self, reason: str = ATTENDANCE_UNKNOWN) -> None:
        """Move the process into the background clamp, latching on success."""
        if self._runner([self._bin, "-b", "-p", str(self.pid)]):
            self._clamped = True
            _register_clamped(self.pid, self._is_running,
                              releasable=self._releasable)
            self._log.info(
                "someone needs the host (reason=%s); clamped encode pid %d "
                "(latched_after_seconds=%.3f)",
                reason, self.pid, max(0.0, self._clock() - self._started_at))
        else:
            self._log.warning("could not clamp pid %d; will retry on the next poll",
                              self.pid)

    def _release(self, *, verified_copy: bool = False) -> None:
        """Lift the background clamp, for encodes that get their speed back."""
        if self._runner([self._bin, "-B", "-p", str(self.pid)]):
            if verified_copy:
                self._releasable = True
            self._clamped = False
            _forget_clamped(self.pid)
            self._log.info("host is idle again; released the clamp on encode pid %d",
                           self.pid)
        else:
            self._log.warning("could not release the clamp on pid %d; will retry",
                              self.pid)

    def _attendance(self) -> tuple[bool, str]:
        """Read attendance once; every failure remains occupied."""
        try:
            attended = self.is_attended()
            reason = _attendance_reason(self.is_attended)
        except Exception as exc:  # noqa: BLE001 - never let a probe kill the encode
            self._log.warning("attendance check raised (%s: %s); clamping to be safe",
                              type(exc).__name__, exc)
            attended = None
            reason = ATTENDANCE_UNKNOWN
        return attended is None or attended, reason

    def _liveness(self, on_error: str) -> bool:
        """Read is_running(), logging and failing closed if the probe raises."""
        try:
            return self._is_running()
        except Exception as exc:  # noqa: BLE001 - unreadable state retains clamp
            self._log.warning("encode liveness check raised (%s: %s); %s",
                              type(exc).__name__, exc, on_error)
            return False

    def _verified_copy_can_release(self) -> bool:
        return self._liveness("retaining clamp") and not stuck_encode_clamped(self.pid)

    def _restart_can_proceed(self) -> bool:
        return self._liveness("not restarting")

    def _restart(self) -> None:
        """Kill the clamped encode; :attr:`restarted` tells the process owner
        (encode_priority._run_supervised) to relaunch it unclamped. This guard
        only has the pid, never the Popen needed to relaunch it itself."""
        self._log.info(
            "clamped early enough that a fresh process should finish sooner; "
            "restarting encode pid %d unclamped", self.pid)
        self._killer(self.pid)
        self._restarted = True

    @property
    def restarted(self) -> bool:
        """Whether this guard killed its encode for an early-clamp restart."""
        return self._restarted

    def _phase_recovery_tick(self, occupied: bool, reason: str) -> None:
        """Apply only a verified copy-tail release or a verified restart."""
        try:
            decision = self._phase_recovery.evaluate(
                clamped=self._clamped,
                prior_latch=LATCH_HARDWARE_ONE_WAY,
                attendance_reason=reason,
            )
        except Exception as exc:  # noqa: BLE001 - an injected seam must fail closed
            self._log.warning("phase recovery observation failed (%s: %s); retaining clamp",
                              type(exc).__name__, exc)
            return
        if _safe_copy_release(decision, occupied, reason) and self._verified_copy_can_release():
            current_occupied, current_reason = self._attendance()
            if current_occupied or current_reason != ATTENDANCE_UNATTENDED:
                return
            self._release(verified_copy=True)
            return
        if _safe_hardware_restart(decision, occupied, reason) and self._restart_can_proceed():
            current_occupied, current_reason = self._attendance()
            if current_occupied or current_reason != ATTENDANCE_UNATTENDED:
                return
            self._restart()

    def tick(self) -> None:
        """Run one poll, clamping or releasing as the host's state changes.

        Releasing only happens for a `releasable` encode, i.e. one with no
        hardware session whose throughput actually recovers. For a VideoToolbox
        encode this stays strictly one-way, because lifting does not give the
        session its speed back.

        Every uncertain answer -- probe returns None, probe raises -- means
        "someone might be here", so it clamps and never releases on it. That is
        the same fail-safe rule the launch decision uses.
        """
        if self._restarted:
            return
        if (self._clamped and not self._releasable
                and self._phase_recovery is None):
            return
        occupied, reason = self._attendance()
        if self._clamped and not self._releasable:
            self._phase_recovery_tick(occupied, reason)
            return
        if occupied and not self._clamped and self._is_running():
            self._clamp(reason)
        elif not occupied and self._may_release():
            self._release()

    def _may_release(self) -> bool:
        """Whether lifting this encode's clamp is safe right now.

        Safe means two things, and the second is not about this encode at all:
        it must be able to use the speed, and no sibling must be left behind by
        it taking that speed. Going faster than an encode that can never catch
        up is not a speed-up, it is that encode's starvation.
        """
        return (self._clamped
                and self._releasable
                and self._is_running()
                and not stuck_encode_clamped(self.pid))

    def _loop(self) -> None:
        # A releasable encode keeps polling after clamping, because it can go
        # back the other way; a hardware one is done once clamped. A
        # restarted one is done too: its process is already dead, and the
        # caller that owns the Popen is the only thing left to act on it.
        while (not self._stop.is_set()
               and not self._restarted
               and not (self._clamped and not self._releasable
                        and self._phase_recovery is None)):
            self.tick()
            self._stop.wait(self.poll_seconds)

    def start(self) -> None:
        """Begin polling in a daemon thread. A no-op if already clamped."""
        # A releasable encode keeps watching even while clamped, because it can
        # go back the other way; without this it would sit at zero throughput
        # with nothing looking for the host to go idle again.
        if (self._thread is not None
                or (self._clamped and not self._releasable
                    and self._phase_recovery is None)):
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name=f"clamp-{self.pid}",
                                        daemon=True)
        self._thread.start()

    def stop(self) -> None:
        """Stop polling. Leaves the process policy alone; the encode is ending."""
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self.poll_seconds + 5)
            self._thread = None
        # Deregistered here rather than in _release, so it also covers the
        # SIGKILL and timeout endings -- and strictly AFTER the join, because a
        # tick already in flight would otherwise re-register the pid after the
        # forget and clamp every later encode for the life of the process.
        _forget_clamped(self.pid)

    def __enter__(self) -> "EncodeClampGuard":
        self.start()
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.stop()


def _safe_copy_release(decision, occupied: bool, reason: str) -> bool:
    """Whether a phase-recovery decision is exactly a verified copy-tail release.

    Every field is checked explicitly rather than trusting ``action`` alone:
    a decision object crossing this boundary is the one place an injected
    seam (a test fixture, or a future phase-recovery implementation) could
    smuggle a restart-bearing or otherwise unsafe decision through a release.
    """
    return (
        occupied is False
        and reason == ATTENDANCE_UNATTENDED
        and decision.phase == PHASE_FASTSTART_TAIL
        and decision.action == ACTION_RELEASE_CLAMP
        and decision.next_latch == LATCH_RELEASABLE_COPY
        and decision.restart_allowed is False
        and decision.restart_threshold is None
    )


def _safe_hardware_restart(decision, occupied: bool, reason: str) -> bool:
    """Whether a phase-recovery decision is exactly a verified hardware restart."""
    return (
        occupied is False
        and reason == ATTENDANCE_UNATTENDED
        and decision.phase == PHASE_HARDWARE_ENCODE
        and decision.action == ACTION_RESTART_ENCODE
        and decision.next_latch == LATCH_HARDWARE_ONE_WAY
        and decision.restart_allowed is True
        and decision.restart_threshold is not None
    )
