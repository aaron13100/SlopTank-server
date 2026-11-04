# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Write-progress watchdog for supervised encode processes."""

from __future__ import annotations

import logging
import os
import signal
import threading
import time
from pathlib import Path
from typing import Callable, Sequence

from lib.organize_media_config import (
    ENCODE_HEARTBEAT_SECONDS,
    ENCODE_STALL_SECONDS,
    STREAM_COPY_MIN_BYTES_PER_SECOND,
)


def _default_killer(pid: int) -> None:
    """Terminate a stalled encode. SIGKILL: ffmpeg ignores SIGTERM mid-write
    often enough that a stalled one would otherwise never go away."""
    try:
        os.kill(pid, signal.SIGKILL)
    except OSError as exc:
        logging.getLogger("encode_priority").warning(
            "could not kill stalled encode pid %d: %s", pid, exc)


class EncodeStallWatchdog:
    """Kills an encode that has stopped WRITING, never one that is merely slow.

    A flat wall-clock timeout cannot be chosen soundly for a whole-file rewrite
    on this host. The same stream copy was measured on 2026-09-05 at roughly
    23 MB/s unclamped on an idle box and at 256 KB/s clamped while a second job
    held the disk: a hundredfold spread. Any constant big enough to be safe for
    the slow case lets a genuinely hung ffmpeg sit for most of a day, and any
    constant tight enough to catch the hang kills honest work. The catch-up
    tool picked 1800s and lost its first seven files, each of which needed
    about six hours.

    Output size answers the question the timeout was trying to ask. ffmpeg
    writes continuously during a copy or an encode, so "the output has not
    grown in N seconds" means stuck, at any speed.

    WHY THIS IS NOT ENOUGH ALONE, diagnosed 2026-09-19 (t_260919_232308_921):
    a 7h20m stall (output frozen at 7,035,975,358 bytes, ffmpeg in
    uninterruptible I/O wait, state UN) went completely unkilled despite a
    900s stall_seconds that should have fired it about 28 times over.
    Instrumented reproduction of both candidate causes:
      - An uncaught exception in the poll loop DOES kill this class's
        background thread silently (confirmed live: thread.is_alive() goes
        False within one poll interval) -- but it also fires Python's default
        threading.excepthook, which prints a full traceback to stderr. The
        production log for this incident (redirected stderr, covering the
        entire 21:05-23:19 stall window) contains zero such tracebacks, so
        this was NOT what happened here. (_loop now catches and logs this
        case anyway, since it is a real independent failure mode; see below.)
      - _progress_stamp's self.path.stat() call has no bound of its own. When
        the underlying volume enters the same uninterruptible-I/O condition
        that parks ffmpeg in state UN, a stat() on a file on that SAME volume
        blocks in the identical kernel wait -- confirmed live: with stat()
        patched to block indefinitely, the thread stays alive
        (thread.is_alive() stays True) but never reaches the comparison or
        the kill, and stop()'s own join(timeout=poll_seconds + 5) returns
        without actually stopping it. This produces exactly zero log output,
        matching the incident log precisely (no exception, no "has not
        written" warning, nothing, for the whole 7h20m).
    A single background thread calling a blocking filesystem syscall cannot
    distinguish that wedge from thread starvation. ``encode_priority`` asks
    for one bounded recovery tick when this cache goes stale: a responsive
    poll confirms a real stall or refreshes recent progress, while a blocked
    poll remains indeterminate and never destroys possibly-progressing media.
    The lock-holding backfill entry points have a separate normal-priority
    ``run_under_hard_deadline.sh`` wrapper, outside the background-priority
    Python process, to end an actually wedged run and release its shared lock.

    :param pid: PID of the encode, killed on stall.
    :param path: The output file the encode is writing.
    :param stall_seconds: Quiet period that counts as stuck.
    :param poll_seconds: Delay between size checks.
    :param killer: Callable taking a pid, injected for tests.
    :param clock: Monotonic time source, injected for tests.
    :param log: Logger for the kill.
    :param stamp: Replaces the output file's (size, mtime) as the progress
        stamp, for an encode with no output file to watch (see
        lib.ffmpeg_progress_report); ``path`` then only names it in logs.
    """

    def __init__(self, pid: int, path, *, stall_seconds: float,
                 poll_seconds: float = 5.0,
                 killer: Callable[[int], None] | None = None,
                 clock: Callable[[], float] | None = None,
                 log: logging.Logger | None = None,
                 is_running: Callable[[], bool] | None = None,
                 stamp: Callable[[], tuple[int, int]] | None = None,
                 heartbeat_seconds: float | None = ENCODE_HEARTBEAT_SECONDS):
        self.pid = pid
        self.heartbeat_seconds = heartbeat_seconds
        self._stamp = stamp
        self.path = Path(path)
        self.stall_seconds = stall_seconds
        self.poll_seconds = poll_seconds
        self._killer = killer or _default_killer
        self._clock = clock or time.monotonic
        self._log = log or logging.getLogger("encode_priority")
        self._is_running = is_running or (lambda: True)
        self._last_stamp = (-1, -1)
        self._last_progress = self._clock()
        self._last_poll = self._clock()
        self._started = self._last_poll
        self._last_heartbeat = self._last_poll
        self._stalled = False
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

    @property
    def stalled(self) -> bool:
        """Whether this watchdog killed the encode for lack of progress."""
        return self._stalled

    def seconds_since_progress(self) -> float:
        """Age, in seconds, of the last observed change to the output.

        Deliberately reads CACHED state (``self._last_progress``), never a
        fresh ``self.path.stat()`` -- see the class docstring's "WHY THIS IS
        NOT ENOUGH ALONE" section: a fresh stat() here could block on the
        exact wedge this reading exists to survive. This is the seam
        encode_priority._communicate_under_supervision's growth-extension
        loop (t_260920_052208_901) polls to decide whether the outer bound
        may keep waiting: as long as this stays under ``stall_seconds``, the
        output is progressing by this watchdog's own definition, at any
        speed, so the outer bound must not treat elapsed wall-clock time
        alone as evidence of trouble (see stream_copy_outer_timeout_seconds'
        docstring for why no wall-clock multiplier can be calibrated safely
        instead).
        """
        return self._clock() - self._last_progress

    def seconds_since_poll(self) -> float:
        """Age, in seconds, of this watchdog's last COMPLETED output check.

        Cached like ``seconds_since_progress``. A healthy poll thread keeps
        this near ``poll_seconds``; a thread that died, or is blocked inside
        stat() on a wedged volume (t_260919_232308_921), lets it grow without
        bound. encode_priority's growth-extension loop reads it to tell "the
        watchdog is alive and will act on a stall itself" from "nothing is
        watching any more", so the loop's own kill stays a backstop for the
        second case and never races the watchdog in the first
        (t_260921_182721_855).
        """
        return self._clock() - self._last_poll

    def describe_last_known_progress(self) -> str:
        """Human-readable (size, mtime) as of the last successful poll.

        Deliberately reads CACHED state (``self._last_stamp`` /
        ``self._last_progress``), never a fresh ``self.path.stat()``. The one
        caller of this (the outer timeout's TIMEOUT log line, see
        encode_priority._communicate_under_supervision) fires in exactly the
        case this watchdog's own poll might be blocked inside that very
        stat() call on a wedged volume (t_260919_232308_921) -- a log line
        that can itself hang would defeat the one bound that is supposed to
        survive that.
        """
        size, mtime_ns = self._last_stamp
        if size < 0:
            return f"{self.path.name} had not appeared as of the last successful poll"
        age = self.seconds_since_progress()
        return (f"{self.path.name} size={size} mtime_ns={mtime_ns} "
                f"({age:.0f}s since last observed change)")

    def _progress_stamp(self) -> tuple[int, int]:
        """Size and mtime of the output; ``(-1, -1)`` while it does not exist.

        Both, not size alone. ffmpeg finishes a ``-movflags +faststart`` output
        by relocating the moov atom, which REWRITES the completed file in place:
        it stops growing for the whole relocation while still being written
        every second. A size-only check reads that as no progress at all and
        kills a healthy encode in its final minutes.

        Measured 2026-09-06 on a 2.65 GB output: one size held for ten minutes
        of the 900 s production window, mtime advancing on every sample, the
        process in uninterruptible I/O wait with both a read and a write
        descriptor open on its own output. Five and a half hours of encoding at
        roughly 90% completion were that close to being SIGKILLed and reported
        as a stall.

        Any CHANGE counts, not just an increase, because a rewrite can shorten
        the file as legitimately as it can extend it.
        """
        if self._stamp is not None:
            return self._stamp()
        try:
            status = self.path.stat()
        except OSError:
            return (-1, -1)
        return (status.st_size, status.st_mtime_ns)

    def _heartbeat(self, stamp: tuple[int, int]) -> None:
        """Log elapsed time and output size once per heartbeat interval.

        A never-finishing encode is otherwise invisible: the stall watchdog
        only speaks when it kills. The output byte count over time is the
        encode's throughput, readable straight from the log.
        """
        if self.heartbeat_seconds is None:
            return
        now = self._clock()
        if now - self._last_heartbeat < self.heartbeat_seconds:
            return
        self._last_heartbeat = now
        size = max(stamp[0], 0)
        self._log.info(
            "encode pid %d running %.0fs; %s is %d bytes",
            self.pid, now - self._started, self.path.name, size)

    def tick(self) -> None:
        """One poll: note growth, or kill if the quiet period has elapsed."""
        if self._stalled:
            return
        stamp = self._progress_stamp()
        self._last_poll = self._clock()
        self._heartbeat(stamp)
        if stamp != self._last_stamp:
            self._last_stamp = stamp
            self._last_progress = self._clock()
            return
        if self._clock() - self._last_progress < self.stall_seconds:
            return
        if not self._is_running():
            return
        self._stalled = True
        self._log.warning(
            "encode pid %d has not written to %s for %.0fs; killing it as stalled",
            self.pid, self.path.name, self.stall_seconds)
        self._killer(self.pid)

    def _loop(self) -> None:
        while not self._stop.is_set() and not self._stalled:
            try:
                self.tick()
            except Exception:
                # An uncaught exception here would otherwise kill this daemon
                # thread SILENTLY (confirmed 2026-09-19: Python's default
                # threading.excepthook does print a traceback to stderr, but
                # nothing in this codebase guarantees that stream reaches a
                # durable log, and the encode is left with zero supervision
                # from this watchdog from that point on). Route it through
                # the real logger instead, and keep polling on the next tick
                # rather than exiting the loop, in case the fault was
                # transient (e.g. a path briefly unreadable during a rename).
                # This does NOT cover a poll that BLOCKS instead of raising
                # (see the class docstring's "why this is not enough alone"
                # note). The bounded recovery tick fails safe in that case;
                # the lock-holding backfill wrappers provide the external
                # hard deadline that can release a genuinely wedged run.
                self._log.exception(
                    "stall watchdog for pid %d crashed while polling %s; "
                    "continuing to poll; a lock-holding backfill run retains "
                    "its external hard-deadline backstop", self.pid, self.path)
            self._stop.wait(self.poll_seconds)

    def start(self) -> None:
        """Begin polling in a daemon thread."""
        if self._thread is not None:
            return
        self._stop.clear()
        self._last_progress = self._clock()
        self._last_poll = self._clock()
        self._thread = threading.Thread(target=self._loop, name=f"stall-{self.pid}",
                                        daemon=True)
        self._thread.start()

    def stop(self) -> None:
        """Stop polling."""
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=self.poll_seconds + 5)
            self._thread = None

    def __enter__(self) -> "EncodeStallWatchdog":
        self.start()
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.stop()


def _uses_hardware_session(argv: Sequence[str]) -> bool:
    """Whether this command negotiates a VideoToolbox session at exec.

    This is what decides if the background clamp is reversible. A VideoToolbox
    encoder fixes its QoS when the session opens, so lifting the clamp later
    does not restore throughput and the guard must stay one-way. Everything
    else -- a stream copy, an audio-only re-encode, a software encode --
    recovers as soon as the clamp comes off.

    That distinction is not cosmetic. Measured 2026-09-06: a loudness pass
    (-c copy video, audio only) clamped by a transient attendance blip ran at
    0 KB/s for hours while holding the shared conversion lock; lifting the
    clamp by hand restored 332 KB/s and took its ETA from ~30 hours to ~81
    minutes.
    """
    return any("videotoolbox" in str(a).lower() for a in argv)


def stream_copy_outer_timeout_seconds(
        size_bytes: int,
        *, min_bytes_per_second: float = STREAM_COPY_MIN_BYTES_PER_SECOND,
        floor_seconds: float = ENCODE_STALL_SECONDS,
        ceiling_multiplier: float = 20.0) -> float:
    """A generous, size-derived number -- no longer an expected duration or a
    kill trigger on its own.

    t_260920_052208_901: this used to be "2x the expected duration at the
    worst-observed throughput" and it SIGKILLed a live, healthy encode ("The
    Old Guard 2 (2025)", pid 90387) that was writing at 181,656 B/s, 4.2x
    slower than STREAM_COPY_MIN_BYTES_PER_SECOND's worst SAMPLE. Root cause,
    diagnosed with a live control (ffprobe on a 47,850-byte LOCAL-disk file:
    0.372s at normal priority, still running at 2m23s -- and a second run
    never finished a 400s budget -- under Darwin's PRIO_DARWIN_BG): the
    backfill pipeline puts itself into Darwin's background I/O tier
    (tools/backfill-streaming.py's ``_self_throttle_disk_io``), which every
    forked child inherits, and background-tier I/O is not slowed
    proportionally under contention, it is STARVED by a factor that has no
    ceiling and depends on what else is touching the device at that moment.
    No throughput floor -- measured idle, measured under one specific load,
    or measured under any other single condition -- can be multiplied into
    a wall-clock number that is safe against a starvation factor like that.
    Raising the multiplier again would only move where the same bug bites.

    So this value no longer decides whether to kill anything by itself. The
    kill decision is composed entirely from EncodeStallWatchdog's own cached
    progress (see EncodeStallWatchdog.seconds_since_progress and
    encode_priority._wait_with_growth_extension, used for every run_encode
    call that passes ``progress_path=`` unless it explicitly opts out with
    ``extend_while_progressing=False``; t_260921_182721_855): as long
    as the output has grown inside the watchdog's own ``stall_seconds``
    window, the wait continues at ANY speed, indefinitely, with no wall-clock
    cap -- the same tolerance the watchdog's stall check already gives a
    hundredfold spread, now extended to a spread with no known bound. This
    function's return value, in that mode, only bounds how often the
    recheck happens (never looser than ``stall_seconds`` itself); a process
    can run far longer than this number without ever being at risk, as long
    as it keeps writing.

    A caller with no watchdog (bare ``timeout=``, e.g. media_probe's
    null-muxer volumedetect) still gets the OLD, non-extending behavior from
    this same number -- a flat wall-clock kill, just far more generous than
    before. Real protection for a stream copy comes from attaching
    ``progress_path=``, not from this function alone.

    :param size_bytes: The SOURCE file's size. A stream copy's output tracks
        this closely enough for the fallback flat-deadline case; the
        appended AAC track(s) this operation adds are a few hundred KB
        against multi-GB inputs.
    :param min_bytes_per_second: Calibration constant, overridable for tests;
        defaults to the measured worst-of-14-idle/normal-load-runs case (see
        STREAM_COPY_MIN_BYTES_PER_SECOND's docstring for the source data).
        Deliberately NOT recalibrated from the loaded/starved measurement
        above -- per that measurement's own finding, no single throughput
        sample generalizes, so this is no longer used as a kill bound at all
        for a caller with a watchdog attached.
    :param floor_seconds: Minimum budget regardless of size, so a small file
        is never given a timeout so tight that ordinary process-launch
        overhead alone could trip it in the no-watchdog fallback case.
        Defaults to ENCODE_STALL_SECONDS, the shortest interval this project
        already treats as meaningful for an encode.
    :param ceiling_multiplier: Headroom over the naive worst-observed-rate
        duration, used only by the no-watchdog fallback case. 20x (was 2x)
        because that fallback should also fail toward more generous, not
        because this is a tight bound anywhere it matters now.
    :returns: Seconds, never below ``floor_seconds``.
    """
    if size_bytes < 0:
        raise ValueError(f"size_bytes must not be negative, got {size_bytes}")
    expected = size_bytes / min_bytes_per_second
    return max(floor_seconds, ceiling_multiplier * expected)
