# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Run cancellation for media-tool children: the error, the scope, the waits.

A run (a bounded backfill pass) carries one cancellation signal. Every child
process a conversion starts must die with the run instead of outliving its
hard deadline (t_260921_182721_855), so the pieces that make that true live
here: ``EncodeCancelled``, the per-thread ``cancel_scope`` that binds a run's
event, the sliced ``wait_checking_cancel`` wait, and
``run_cancellable_capture`` for read-only probes.

This is a leaf module (standard library only). encode_priority (the
supervised encode runner) and media_duration (a leaf of the probe pipeline)
both need these names, and neither may import the other, so they import this.
"""

from __future__ import annotations

import contextlib
import logging
import os
import signal
import subprocess
import threading
import time
from pathlib import Path

#: Same logger name as encode_priority and transcode, so the CANCELLED lines
#: stay together with the rest of the encode-supervision output.
log = logging.getLogger("organize-media")


class EncodeCancelled(Exception):
    """A run's cancellation signal fired while this encode was waiting.

    Distinct from subprocess.TimeoutExpired (an outer bound firing on a
    process that is merely slow) and from an ordinary conversion failure:
    backfill_execution._harvest must not record this as a per-file failure
    strike, since the file was never actually attempted to completion, only
    stopped by the run itself (SIGTERM, or a hard drain deadline).
    """

    def __init__(self, reason: str):
        super().__init__(f"cancelled: {reason}")
        self.reason = reason


#: Per-thread fallback cancel_event for run_encode calls made from deep
#: inside a pipeline stage (media_probe, container_remux, subtitle_embed,
#: audio_reorder, ...) that has no cancel_event parameter of its own. See
#: cancel_scope. run_encode's own explicit ``cancel_event=`` argument always
#: wins over this when a caller passes one directly.
_active_cancel_event = threading.local()


@contextlib.contextmanager
def cancel_scope(cancel_event: threading.Event | None):
    """Make ``cancel_event`` run_encode's implicit default on this thread.

    i261: every run_encode call reachable from inside one bounded backfill
    run must honor that run's cancellation, but adding a new keyword to each
    intermediate module's own call signature (media_probe, container_remux,
    subtitle_embed, subtitle_pipeline, audio_reorder, audio_normalize,
    tv_export_subtitles) is the mechanical-sweep shape this project's
    siblings rule exists to avoid, and each omission would be a silent gap.
    run_encode is already documented as "the only way in" for every encode;
    this makes it the only place cancellation needs wiring too. Re-entrant:
    a nested scope restores the enclosing one on exit.
    """
    previous = bound_cancel_event()
    _active_cancel_event.value = cancel_event
    try:
        yield
    finally:
        _active_cancel_event.value = previous


def bound_cancel_event() -> threading.Event | None:
    """The cancel event ``cancel_scope`` bound on this thread, or None."""
    return getattr(_active_cancel_event, "value", None)


#: How often a cancellable wait rechecks cancel_event when nothing else would
#: otherwise wake it (the plain, non-growth-extension path below). Small
#: enough that "kills the child within one poll" (i261 item 2) is true in
#: practice, large enough not to spin.
CANCEL_POLL_SECONDS = 5.0


def wait_checking_cancel(proc, cmd: list[str], timeout: float | None,
                         cancel_event: threading.Event,
                         poll_seconds: float = CANCEL_POLL_SECONDS,
                         clock=time.monotonic):
    """communicate() in slices so a fired cancel_event interrupts within one poll.

    Used for callers with no progress watchdog (e.g. media_probe's
    volumedetect: a null-muxer probe has no output file to watch), where
    ``timeout`` is the only existing bound. That bound's exact meaning is
    unchanged -- this only adds an earlier exit when ``cancel_event`` fires
    first. SIGKILL is used because it terminates a SIGSTOPped (clamp-paused)
    child too, unlike SIGTERM, which never gets delivered to a stopped
    process (i261: the killer PID observed stuck 6h+ in state T).
    """
    # Elapsed time is read from ``clock``, never summed from slice budgets:
    # under PRIO_DARWIN_BG this supervising thread is starved as well, every
    # communicate() slice runs long, and a slice count silently stretched a
    # declared 28800s bound past 36,000s of wall time (t_260921_182721_855).
    started = clock()
    while True:
        remaining = None if timeout is None else max(timeout - (clock() - started), 0.0)
        if remaining is not None and remaining <= 0:
            raise subprocess.TimeoutExpired(cmd, timeout)
        slice_timeout = poll_seconds if remaining is None else min(poll_seconds, remaining)
        try:
            return proc.communicate(timeout=slice_timeout)
        except subprocess.TimeoutExpired:
            if cancel_event.is_set():
                log.warning(
                    "CANCELLED: killing encode pid %d (run cancellation signal "
                    "fired; t_260921_182721_855)", proc.pid)
                proc.kill()
                proc.wait()
                raise EncodeCancelled(f"encode pid {proc.pid}") from None


#: Poll slice for run_cancellable_capture. Probes are short and this loop does
#: no supervision work, so a 1 s slice makes "dies within a poll of the
#: cancellation" cheap (run_encode's 5 s slice serves multi-hour encodes).
CAPTURE_CANCEL_POLL_SECONDS = 1.0


def _kill_process_group(proc) -> None:
    """SIGKILL ``proc``'s whole process group (it leads its own session).

    A probe wrapper (ffsubsync, a shell script) can have children of its own
    that hold the pipes open; killing only the leader would leave
    ``communicate()`` blocked on them. Falls back to the leader alone when
    the group is already gone.
    """
    try:
        os.killpg(proc.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        proc.kill()


def run_cancellable_capture(cmd: list[str], *, timeout: float | None,
                            text: bool = True,
                            cancel_event: threading.Event | None = None,
                            poll_seconds: float = CAPTURE_CANCEL_POLL_SECONDS,
                            ) -> subprocess.CompletedProcess:
    """Run a read-only probe, capturing stdout/stderr, honoring run cancellation.

    The sibling of :func:`run_encode` for media-tool children that need no
    priority/clamp supervision (ffprobe, capability queries, helper scripts)
    but MUST still die with the run: t_260921_182721_855 found a whole-
    container ``ffprobe`` packet read, started through a raw
    ``subprocess.run``, running 28+ minutes past the run's hard deadline and
    pinning the shared backfill lock, because only ``run_encode`` children
    knew about cancellation. Every media-tool child reachable from a
    conversion goes through one of these two runners
    (tests/test_conversion_children_cancellable.py enforces it).

    Outside any ``cancel_scope`` (and with no ``cancel_event``) this is
    exactly ``subprocess.run(cmd, capture_output=True, text=text,
    timeout=timeout)``. Inside one, the child leads its own session so a
    cancellation (or timeout) SIGKILLs it and its descendants together.

    :param cmd: argv, no priority prefix.
    :param timeout: Seconds before the child is killed and
        ``subprocess.TimeoutExpired`` raised (same contract as subprocess.run;
        None means unbounded).
    :param text: Decode stdout/stderr as text; False for raw bytes.
    :param cancel_event: Explicit cancellation signal; defaults to the one
        bound on this thread by ``cancel_scope``.
    :param poll_seconds: Cancellation poll slice; tests shrink it.
    :returns: CompletedProcess with captured streams.
    :raises EncodeCancelled: the signal was set before launch or while waiting;
        the child is dead when this raises.
    :raises subprocess.TimeoutExpired: ``timeout`` elapsed; the child is dead.
    """
    event = (cancel_event if cancel_event is not None
             else bound_cancel_event())
    if event is None:
        return subprocess.run(cmd, capture_output=True, text=text, timeout=timeout)
    if event.is_set():
        raise EncodeCancelled(f"before launching {Path(cmd[0]).name}")
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=text, start_new_session=True)
    try:
        # communicate() drains both pipes concurrently on every slice, so a
        # multi-MB JSON answer cannot deadlock on a full pipe buffer.
        stdout, stderr = wait_checking_cancel(
            proc, cmd, timeout, event, poll_seconds=poll_seconds)
    except subprocess.TimeoutExpired:
        _kill_process_group(proc)
        stdout, stderr = proc.communicate()
        raise subprocess.TimeoutExpired(cmd, timeout, output=stdout, stderr=stderr) from None
    except BaseException:
        # EncodeCancelled (leader already killed by wait_checking_cancel),
        # KeyboardInterrupt, anything else: no descendant may outlive us.
        _kill_process_group(proc)
        proc.communicate()  # reaps the leader and closes both pipes
        raise
    return subprocess.CompletedProcess(cmd, proc.returncode, stdout, stderr)
