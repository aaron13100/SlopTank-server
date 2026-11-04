# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Scale a subprocess wall-clock timeout by measured machine contention.

This host has no quiet period (CLAUDE.md: "there is no such thing as a quiet
box on this machine and that will never happen"), so a fixed timeout tuned
for an idle machine intermittently reads a slow-but-healthy read as a hang.
Mirrors tests/test_encode_entry_point.py's ``_load_scaled_timeout`` (moved to
tests/load_scaled_bounds.py) and the queue-runner's own
``qr_resolve_load_scaled_deadline`` formula: base x clamp(load1/ncpu, 1x,
scale_max). This is the production (non-test) copy, for callers under
tools/lib that need the same scaling at runtime rather than in a test bound.
"""

from __future__ import annotations

import os
import sys
import time
from typing import Callable


def load_scale(*, scale_max: float = 20.0) -> float:
    """Return clamp(1-min load average per core, 1x, ``scale_max``) right now.

    1.0 when the load average cannot be read, so an unmeasurable host is
    treated as idle rather than as infinitely slow.
    """
    try:
        load1, _, _ = os.getloadavg()
        ncpu = os.cpu_count()
    except OSError:
        return 1.0
    return max(1.0, min(load1 / (ncpu or 1), scale_max))


class LoadAdjustedBudget:
    """A time budget stated in idle-host seconds, consumed on a load-adjusted clock.

    ``load_scaled_timeout`` scales a bound once, from the load at the moment
    it is computed, so load that rises mid-run still expires a healthy
    command: a promotion's bundle signing got a 229s budget at start and was
    stopped at 229s while the host's load climbed (2026-09-28). This budget is
    re-scaled continuously instead: every ``tick`` charges the wall time since
    the previous tick divided by the load scale measured now, so a stretch at
    load 10 per core costs a tenth of what it would on an idle host. It still
    expires eventually (the scale is capped at ``scale_max``), so a wedged
    command is stopped after at most ``base_seconds * scale_max`` of wall time.

    One budget may be shared by several sequential steps (each ticks it while
    it runs), which is how a per-project test deadline spans build, discovery
    and every shard.
    """

    def __init__(
        self,
        base_seconds: float,
        *,
        scale_max: float = 20.0,
        monotonic: Callable[[], float] = time.monotonic,
        scale: Callable[..., float] = load_scale,
    ) -> None:
        """Start the budget now.

        :param base_seconds: The bound that would be correct on an idle host.
        :param scale_max: Cap on the load multiplier.
        :param monotonic: Clock seam; defaults to ``time.monotonic``.
        :param scale: Load seam; called as ``scale(scale_max=...)``.
        """
        self.base_seconds = float(base_seconds)
        self._scale_max = scale_max
        self._monotonic = monotonic
        self._scale = scale
        self._consumed = 0.0
        self._last = monotonic()

    def tick(self) -> None:
        """Charge the wall time since the last tick at the current load scale."""
        now = self._monotonic()
        elapsed = max(0.0, now - self._last)
        self._last = now
        self._consumed += elapsed / self._scale(scale_max=self._scale_max)

    def remaining(self) -> float:
        """Idle-host seconds left after charging time up to now; never negative."""
        self.tick()
        return max(0.0, self.base_seconds - self._consumed)

    def expired(self) -> bool:
        """True once the whole budget has been charged."""
        return self.remaining() <= 0.0


def load_scaled_timeout(base_seconds: float, *, scale_max: float = 20.0) -> float:
    """Return ``base_seconds`` scaled up for the machine's current load.

    Computed fresh on every call (never cache the result): load changes
    second to second, so a value memoized at import or as a default
    parameter would freeze whatever contention happened to exist then.

    :param base_seconds: The bound that would be correct on an idle host.
    :param scale_max: Upper bound on the multiplier, so a pathological load
        spike still fails eventually rather than waiting forever.
    :returns: ``base_seconds`` times clamp(1-min load average per core, 1x,
        ``scale_max``); falls back to the unscaled base when the load
        average cannot be read.
    """
    return base_seconds * load_scale(scale_max=scale_max)


#: Poll spacing for ``--run``, and the grace between SIGTERM and SIGKILL
#: (matching the ``timeout --kill-after=30s`` this entry point replaced).
RUN_POLL_SECONDS = 1.0
RUN_KILL_AFTER_SECONDS = 30.0
RUN_TIMED_OUT_EXIT = 124


def run_with_budget(base_seconds: float, command: list[str]) -> int:
    """Run ``command`` under a LoadAdjustedBudget; return its exit status.

    The load-adjusted counterpart of ``timeout(1)``, for shell wrappers:
    SIGTERM once ``base_seconds`` of idle-host time are charged, SIGKILL
    after a further RUN_KILL_AFTER_SECONDS, and exit 124 as ``timeout`` does.
    A SIGTERM or SIGHUP sent to this process is passed on to the command.
    Standard library only, so a wrapper can run it without the tools' imports.
    """
    import signal
    import subprocess

    process = subprocess.Popen(command)

    def forward(signum: int, _frame: object) -> None:
        process.send_signal(signum)

    for signum in (signal.SIGTERM, signal.SIGHUP):
        signal.signal(signum, forward)
    budget = LoadAdjustedBudget(base_seconds)
    while True:
        try:
            return process.wait(timeout=RUN_POLL_SECONDS)
        except subprocess.TimeoutExpired:
            if budget.expired():
                break
    process.terminate()
    try:
        process.wait(timeout=RUN_KILL_AFTER_SECONDS)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    return RUN_TIMED_OUT_EXIT


def main(arguments: list[str]) -> int:
    """``load_scaled_timeout.py --run SECONDS -- COMMAND...``."""
    if len(arguments) < 4 or arguments[0] != "--run" or arguments[2] != "--":
        print("usage: load_scaled_timeout.py --run SECONDS -- COMMAND...", file=sys.stderr)
        return 2
    try:
        seconds = float(arguments[1])
    except ValueError:
        print(f"invalid budget {arguments[1]!r}; expected seconds", file=sys.stderr)
        return 2
    if not seconds > 0:
        print(f"invalid budget {arguments[1]!r}; expected positive seconds", file=sys.stderr)
        return 2
    return run_with_budget(seconds, arguments[3:])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
