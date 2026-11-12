# SlopTank modification notice: added or changed by SlopTank on 2026-11-12.
"""Shared deadline and bounded subprocess execution for macOS smoke drivers."""

from __future__ import annotations

import subprocess
import time

from lib.load_scaled_timeout import load_scaled_timeout


POLL_SECONDS = 0.5


class SmokeError(RuntimeError):
    """An acceptance step failed with its observed diagnostic."""


class Deadline:
    """One wall-clock deadline shared by commands, HTTP requests, and polling."""

    def __init__(self, seconds: float):
        self.at = time.monotonic() + seconds

    def remaining(self) -> float:
        return self.at - time.monotonic()

    def timeout(self, maximum: float, label: str) -> float:
        remaining = self.remaining()
        if remaining <= 0:
            raise SmokeError(f"Overall smoke deadline expired before {label}.")
        return min(load_scaled_timeout(maximum), remaining)


def run(argv: list[str], label: str, deadline: Deadline, timeout: float = 60,
        *, check: bool = True, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    """Run a bounded command and retain diagnostics only when it fails."""
    try:
        result = subprocess.run(argv, text=True, capture_output=True, stdin=subprocess.DEVNULL, env=env,
                                timeout=deadline.timeout(timeout, label), check=False)
    except subprocess.TimeoutExpired as error:
        stderr = error.stderr.decode(errors="replace") if isinstance(error.stderr, bytes) else error.stderr or ""
        raise SmokeError(f"{label} timed out: {stderr[-1500:]}") from error
    if check and result.returncode:
        raise SmokeError(f"{label} exited {result.returncode}: {result.stderr[-2000:]} {result.stdout[-1000:]}")
    return result
