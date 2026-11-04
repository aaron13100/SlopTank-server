# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Running one encode under supervision: launch clamp, guard, watchdog, restart.

FAIL-SAFE INVARIANT
-------------------
Every uncertain path (unreachable server, unparseable response, raised
exception, failed taskpolicy call) resolves to CLAMPED, which is the old
unconditional behaviour. A broken supervisor can only cost throughput, never
playback quality. See encode_launch_policy.py for why the launch-time clamp
choice cannot be revisited later, and encode_clamp.py for the guard that
watches a running encode and clamps it (one-way) the moment anyone needs the
machine.
"""

from __future__ import annotations

import logging
import shutil
import tempfile
import subprocess
import threading
import time
from pathlib import Path

from lib.encode_cancellation import (
    CANCEL_POLL_SECONDS,
    EncodeCancelled,
    bound_cancel_event,
    wait_checking_cancel,
)
from lib.encode_attendance import (
    PlaybackCheck,
    _ATTENDANCE_REASONS,
    _TOOL_FALLBACKS,
    _attendance_reason,
    _default_runner,
    host_idle_seconds,
    jellyfin_playback_check,
    resolve_tool,
    unattended_check,
)
from lib.encode_clamp import (
    DEFAULT_POLL_SECONDS,
    EncodeClampGuard,
)
from lib.encode_clamp_registry import (
    _ClampedEncode,
    _CLAMPED_ENCODES,
    _CLAMPED_LOCK,
    _forget_all_clamped,
    _forget_clamped,
    _prune_dead_locked,
    _register_clamped,
    any_encode_clamped,
    stuck_encode_clamped,
)
from lib.encode_launch_policy import (
    _clamp_at_launch,
    _host_attended_check,
    _priority_prefix,
)
from lib.encode_watchdog import (
    EncodeStallWatchdog,
    _default_killer,
    _uses_hardware_session,
)
from lib.encode_phase_supervisor import build_injected_supervisor
from lib.encode_phase_observer import default_phase_recovery_factory
from lib.encode_process import launch_process, terminate_and_reap
from lib.ffmpeg_progress_report import progress_stamp_reader, with_progress_report
from lib.organize_media_config import (
    ADAPTIVE_ENCODE_THROTTLE,
    ADAPTIVE_THROTTLE_POLL_SECONDS,
    ENCODE_STALL_POLL_SECONDS,
    ENCODE_STALL_SECONDS,
)

#: Shared with transcode.py, which re-exports these; one logger keeps the
#: encode-supervision lines together whichever module called in.
log = logging.getLogger("organize-media")

# Keep the process-launch seams on the public facade for private tests and
# callers while the implementation remains isolated in encode_process.
_launch_process = launch_process
_terminate_and_reap = terminate_and_reap


def _encode_supervisors(
    proc,
    cmd: list[str],
    *,
    launched_clamped: bool,
    runner,
    progress_path,
    stall_seconds: float,
    poll_seconds: float | None,
    attended_check: PlaybackCheck | None,
    phase_recovery_factory=None,
    allow_restart: bool = True,
    progress_stamp=None,
):
    """Create the attendance clamp and optional progress watchdog."""
    guard = EncodeClampGuard(
        proc.pid,
        # A permanent background policy must also constrain the guard: a
        # progress watchdog still creates it when adaptive mode is disabled.
        (attended_check or _host_attended_check()) if ADAPTIVE_ENCODE_THROTTLE
        else (lambda: True),
        launched_clamped=launched_clamped or not ADAPTIVE_ENCODE_THROTTLE,
        poll_seconds=(ADAPTIVE_THROTTLE_POLL_SECONDS if poll_seconds is None
                      else poll_seconds),
        runner=runner,
        log=log,
        releasable=not _uses_hardware_session(cmd),
        is_running=lambda: proc.poll() is None,
        phase_recovery=build_injected_supervisor(
            phase_recovery_factory, proc.pid, tuple(cmd),
            # A progress REPORT is not the encode's output: nothing to observe.
            Path(progress_path) if progress_path is not None
            and progress_stamp is None else None,
            uses_hardware=_uses_hardware_session(cmd), logger=log,
            restart_enabled=allow_restart),
    )
    watchdog = None
    if progress_path is not None:
        watchdog = EncodeStallWatchdog(
            proc.pid, progress_path,
            stall_seconds=stall_seconds,
            poll_seconds=(ENCODE_STALL_POLL_SECONDS if poll_seconds is None
                          else poll_seconds),
            log=log,
            is_running=lambda: proc.poll() is None,
            stamp=progress_stamp,
        )
    return guard, watchdog


def _safe_watchdog_tick(watchdog: EncodeStallWatchdog) -> None:
    """Run one watchdog tick, routing any exception through the real logger.

    The recovery tick in ``_wait_with_growth_extension`` runs in a daemon
    thread: an uncaught exception there would end the thread silently (cf.
    ``EncodeStallWatchdog._loop``'s own catch) and leave the wait believing
    a recovery was attempted that never polled anything.
    """
    try:
        watchdog.tick()
    except Exception:
        log.exception(
            "stall recovery tick for pid %d crashed while polling %s",
            watchdog.pid, watchdog.path)


def _wait_with_growth_extension(proc, cmd: list[str], recheck_seconds: float | None,
                                watchdog, *, cancel_event: threading.Event | None = None):
    """Wait for as long as the output keeps growing; never on a wall clock alone.

    t_260920_052208_901: a flat ``proc.communicate(timeout=X)`` SIGKILLed a
    live, healthy encode ("The Old Guard 2", pid 90387) because X was
    calibrated from throughput samples that Darwin's background I/O tier can
    invalidate by an UNBOUNDED factor (measured live: a 47,850-byte ffprobe
    took 0.372s at normal priority and was still running at 2m23s under the
    inherited background tier, with a second run not finishing a 400s budget
    at all -- see stream_copy_outer_timeout_seconds' docstring). No wall-clock
    number, however generous, is safe against a starvation factor with no
    ceiling, so this deliberately has none: it polls in slices and re-checks
    the watchdog's own CACHED progress stamp (``seconds_since_progress()``,
    never a fresh stat -- same reasoning as ``describe_last_known_progress``)
    every slice, and only a CONFIRMED stall can end the wait. While the
    watchdog is still completing polls it acts on a stall itself, on the
    identical definition of "stuck" its own background thread uses, so this
    is a second CHECK on that one signal, not a second opinion from an
    unrelated one. When BOTH the progress and the poll caches have been
    stale for a full ``watchdog.stall_seconds`` window, c261 (live
    2026-09-25: pid 76820 killed at 1,808s from this exact cached state
    while the starved observer's next poll showed growth 19s earlier)
    forbids raising from the indeterminate cache alone: the loop requests
    one bounded recovery tick instead, a fresh ``watchdog.tick()`` in a
    daemon thread with at most one in flight per wait. A tick that sees
    progress refreshes the caches and the wait continues; a tick on a
    responsive filesystem that sees a genuine stall kills exactly as the
    watchdog's own thread would; a tick whose stat blocks
    (t_260919_232308_921's wedged-volume class) leaves the wait running,
    failing safe against destroying progressing media.

    ``recheck_seconds`` bounds how often that recheck happens; it is never
    the thing that decides to kill (a process with recent growth is waited
    on indefinitely), so a generous, size-derived value here is a courtesy
    to logging/responsiveness, not a safety property. Defaults the slice to
    ``watchdog.stall_seconds`` when None or larger, so the recheck cadence
    can never be looser than the watchdog's own definition of "quiet".
    """
    slice_seconds = watchdog.stall_seconds
    if recheck_seconds is not None:
        slice_seconds = min(slice_seconds, recheck_seconds)
    if cancel_event is not None:
        # c196 (2026-09-25): a stall-window-long slice read the cancel only
        # once per window, so a SIGTERMed run outlived its bound by minutes.
        # A shorter slice changes nothing else: progress just re-loops.
        slice_seconds = min(slice_seconds, CANCEL_POLL_SECONDS)
    # Real elapsed time for the TimeoutExpired report, not a slice count
    # (a starved supervising thread makes every slice run long).
    started = time.monotonic()
    # At most one recovery tick in flight per wait: a tick whose stat()
    # blocks on a wedged volume never returns, so a second one must not be
    # started behind it on every slice.
    recovery_thread: threading.Thread | None = None
    while True:
        try:
            return proc.communicate(timeout=slice_seconds)
        except subprocess.TimeoutExpired:
            elapsed = time.monotonic() - started
            if cancel_event is not None and cancel_event.is_set():
                log.warning(
                    "CANCELLED: killing encode pid %d (run cancellation signal "
                    "fired; t_260921_182721_855)", proc.pid)
                proc.kill()
                proc.wait()
                raise EncodeCancelled(f"encode pid {proc.pid}") from None
            if watchdog.seconds_since_progress() >= watchdog.stall_seconds:
                # No growth for at least one full stall window: either
                # genuinely stuck, or the watchdog's own thread is starved,
                # dead or wedged (t_260919_232308_921) and its cache simply
                # stopped updating. A watchdog still completing polls acts on
                # the stall itself on its next tick and reports it as a
                # stall (t_260921_182721_855: racing it here turned that
                # report into a bare TimeoutExpired), so wait for it.
                if watchdog.seconds_since_poll() < watchdog.stall_seconds:
                    continue
                # Both caches stale: cached observer staleness is not proof
                # of media stalling (c261, live 2026-09-25: pid 76820 was
                # killed at 1,808s from this exact state while the starved
                # observer's next poll showed growth 19s earlier), so never
                # kill from it alone. Request one bounded recovery tick: a
                # fresh watchdog.tick() that refreshes the caches itself if
                # the output moved, or kills exactly as the watchdog's own
                # thread would if a responsive filesystem confirms the stall.
                if recovery_thread is not None and recovery_thread.is_alive():
                    # The recovery stat itself is blocked; the encode's state
                    # is indeterminate, which fails safe toward keeping it.
                    continue
                recovery_thread = threading.Thread(
                    target=_safe_watchdog_tick, args=(watchdog,),
                    name=f"stall-recovery-{watchdog.pid}", daemon=True)
                recovery_thread.start()
                # Bounded: a responsive stat lands far inside one cancel
                # poll; past that the filesystem is indeterminate and the
                # wait continues with the tick still in flight.
                recovery_thread.join(timeout=CANCEL_POLL_SECONDS)
                if recovery_thread.is_alive():
                    continue
                if watchdog.stalled:
                    raise subprocess.TimeoutExpired(cmd, elapsed)
                continue
            # Progressed inside the window per the watchdog's own cache:
            # keep going, at any speed, forever -- see the docstring above
            # for why no wall-clock cap belongs here.
            continue


def _communicate_under_supervision(proc, cmd: list[str], timeout: float | None,
                                   guard, watchdog, *,
                                   extend_while_progressing: bool = False,
                                   cancel_event: threading.Event | None = None):
    """Communicate with one process while always stopping its supervisors.

    :param extend_while_progressing: When True, ``timeout`` stops being a
        flat deadline that can fire on wall-clock time alone; instead
        (see ``_wait_with_growth_extension``) it only bounds how often the
        loop rechecks ``watchdog`` for recent output growth, and only a
        recovery-CONFIRMED stall (both caches stale for a full
        ``stall_seconds`` window, plus a fresh bounded recovery tick
        observing no progress on a responsive filesystem) can end the wait.
        Requires ``watchdog`` (a progress_path must be attached); off by
        default so every existing caller keeps the exact
        single-shot ``communicate(timeout=timeout)`` behaviour unchanged.
    :param cancel_event: When given and set, the wait ends early (killing
        the child with SIGKILL, which works even on a clamp-SIGSTOPped
        child) and raises EncodeCancelled instead of either running to
        completion or raising TimeoutExpired. See cancel_scope.
    """
    if extend_while_progressing and watchdog is None:
        raise ValueError(
            "extend_while_progressing requires a progress watchdog "
            "(pass progress_path=) to read growth from")
    guard.start()
    if watchdog is not None:
        watchdog.start()
    try:
        if extend_while_progressing:
            return _wait_with_growth_extension(
                proc, cmd, timeout, watchdog, cancel_event=cancel_event)
        if cancel_event is not None:
            return wait_checking_cancel(proc, cmd, timeout, cancel_event)
        return proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        # This is the OUTER bound firing (see encode_watchdog's class
        # docstring and stream_copy_outer_timeout_seconds): it is reached by
        # subprocess.communicate()'s own wait on pipe descriptors and
        # os.waitpid(WNOHANG), never by touching the output file, so it fires
        # even when `watchdog` is dead or stuck mid-syscall on a wedged
        # volume. describe_last_known_progress() is deliberately a CACHED
        # read for that same reason: a fresh stat() here could hang on the
        # exact wedge this bound exists to survive. exc.timeout (not the raw
        # `timeout` param) is reported because the growth-extension loop's
        # actual elapsed time when it gives up has no fixed relationship to
        # `timeout` at all (see _wait_with_growth_extension's docstring).
        progress = (watchdog.describe_last_known_progress()
                   if watchdog is not None else "no progress watchdog was attached")
        fired_after = exc.timeout if exc.timeout is not None else timeout
        log.warning(
            "TIMEOUT: encode pid %d exceeded its %.0fs outer bound (%s); "
            "killing with SIGKILL", proc.pid, fired_after, progress)
        proc.kill()
        stdout, stderr = proc.communicate()
        raise subprocess.TimeoutExpired(cmd, fired_after, output=stdout, stderr=stderr)
    finally:
        guard.stop()
        if watchdog is not None:
            watchdog.stop()
        if getattr(guard, "clamped", False) and proc.returncode is not None:
            log.info(
                "encode supervision completed for pid %d (returncode=%d)",
                proc.pid,
                proc.returncode,
            )


def _legacy_launch_state(cmd: list[str]) -> bool:
    """Infer the old launch state for private callers that omit the keyword."""
    taskpolicy_bin = resolve_tool("taskpolicy")
    return (len(cmd) >= 2 and cmd[0] == taskpolicy_bin and cmd[1] == "-b")


def _stalled_completed_process(cmd: list[str], proc, stdout, stderr,
                               progress_path, stall_seconds: float, *,
                               text: bool) -> subprocess.CompletedProcess:
    """Return a visible failure for a watchdog kill whose ffmpeg stderr is empty."""
    note = (f"\nencode stalled: nothing written to {Path(progress_path).name} "
            f"for {stall_seconds:.0f}s; killed by EncodeStallWatchdog\n")
    if text:
        stderr = (stderr or "") + note
    else:
        stderr = (stderr or b"") + note.encode("utf-8")
    return subprocess.CompletedProcess(
        cmd, proc.returncode if proc.returncode else -9, stdout, stderr)


def _strip_clamp_prefix(cmd: list[str]) -> list[str]:
    """Drop a leading ``<taskpolicy> -b`` pair before a confirmed-unattended relaunch.

    Only used for the one relaunch a hardware-restart decision earns. ``cmd``
    is the ORIGINAL launch argv, which may have started clamped (or been
    clamped mid-run) for a reason that no longer holds by the time a restart
    fires: the guard only restarts once attendance has come back
    confirmed-unattended, so re-sending ``-b`` on the relaunch would clamp the
    fresh process at exec and throw the restart away for nothing.
    """
    taskpolicy_bin = resolve_tool("taskpolicy")
    if len(cmd) >= 2 and cmd[0] == taskpolicy_bin and cmd[1] == "-b":
        return cmd[2:]
    return cmd


def _run_supervised(cmd: list[str], timeout: float | None, *,
                    launched_clamped: bool | None = None,
                    runner=None, progress_path=None,
                    stall_seconds: float = ENCODE_STALL_SECONDS,
                    poll_seconds: float | None = None,
                    attended_check: PlaybackCheck | None = None,
                    phase_recovery_factory=None,
                    text: bool = True,
                    allow_restart: bool = True,
                    extend_while_progressing: bool = False,
                    cancel_event: threading.Event | None = None,
                    progress_stamp=None) -> subprocess.CompletedProcess:
    """Run ffmpeg with attendance and progress supervisors.

    ``allow_restart`` bounds a hardware-clamp restart to at most once: the
    relaunch this function makes for itself always passes ``False``, so a
    fresh process that gets clamped again keeps its clamp for the rest of its
    run rather than restarting indefinitely.

    ``extend_while_progressing``: see ``_communicate_under_supervision``.
    ``cancel_event``: see ``_communicate_under_supervision``.
    """
    # This remains a drop-in for subprocess.run(capture_output=True): it keeps
    # CompletedProcess/TimeoutExpired and text-or-bytes stream contracts. The
    # launch clamp was decided by _priority_prefix; this guard can restore it
    # mid-run because taskpolicy/nice exec in place and Popen.pid is ffmpeg.
    # A progress path adds liveness detection independent of wall-clock timeout.
    if not ADAPTIVE_ENCODE_THROTTLE and progress_path is None:
        return subprocess.run(cmd, capture_output=True, text=text, timeout=timeout)

    if launched_clamped is None:
        # Compatibility for private callers predating the explicit state.  The
        # public run_encode path always passes the helper's effective result.
        launched_clamped = _legacy_launch_state(cmd)
    proc, effective_clamped = _launch_process(
        cmd, launched_clamped=launched_clamped, text=text, logger=log)
    try:
        guard, watchdog = _encode_supervisors(
            proc,
            cmd,
            launched_clamped=effective_clamped,
            runner=runner,
            progress_path=progress_path,
            stall_seconds=stall_seconds,
            poll_seconds=poll_seconds,
            attended_check=attended_check,
            phase_recovery_factory=phase_recovery_factory,
            allow_restart=allow_restart,
            progress_stamp=progress_stamp,
        )
    except BaseException:
        _terminate_and_reap(proc)
        raise
    try:
        stdout, stderr = _communicate_under_supervision(
            proc, cmd, timeout, guard, watchdog,
            extend_while_progressing=extend_while_progressing,
            cancel_event=cancel_event)
    except BaseException:
        # Covers failures while starting either supervisor as well as a
        # supervisor callback that raises before its own cleanup can run.
        _terminate_and_reap(proc)
        raise
    if watchdog is not None and watchdog.stalled:
        return _stalled_completed_process(
            cmd, proc, stdout, stderr, progress_path, stall_seconds, text=text)
    if allow_restart and getattr(guard, "restarted", False):
        log.info(
            "relaunching pid %d's encode unclamped after an early-clamp restart",
            proc.pid)
        return _run_supervised(
            _strip_clamp_prefix(cmd), timeout,
            launched_clamped=False, runner=runner, progress_path=progress_path,
            stall_seconds=stall_seconds, poll_seconds=poll_seconds,
            attended_check=attended_check, phase_recovery_factory=phase_recovery_factory,
            text=text, allow_restart=False,
            extend_while_progressing=extend_while_progressing,
            cancel_event=cancel_event, progress_stamp=progress_stamp,
        )
    return subprocess.CompletedProcess(cmd, proc.returncode, stdout, stderr)


def run_encode(argv: list[str], *, timeout: float | None, runner=None,
               progress_path=None, stall_seconds: float = ENCODE_STALL_SECONDS,
               poll_seconds: float | None = None,
               attended_check: PlaybackCheck | None = None,
               phase_recovery_factory=default_phase_recovery_factory,
               text: bool = True,
               extend_while_progressing: bool | None = None,
               cancel_event: threading.Event | None = None,
               progress_report: bool = False) -> subprocess.CompletedProcess:
    """Start an encode at the right priority, supervised. The only way in.

    Splitting this in two is a trap, and it caught us once. The launch-time
    clamp decision (:func:`_priority_prefix`) and the mid-encode guard
    (:func:`_run_supervised`) are two halves of ONE policy: on an idle host the
    prefix may omit ``taskpolicy -b`` only because the launch helper first
    releases the inherited background tier and the guard re-applies the clamp
    the moment a viewer turns up. A caller that took only the prefix and ran it
    itself therefore got the speed with none of the protection, and nothing
    could ever clamp it back down.
    tools/fix-tv-audio-catchup.py did that to 38 multi-GB remuxes.

    Composing both here means the halves cannot be separated, and it lets the
    guard be TOLD whether the launch was clamped instead of sniffing the argv.

    :param argv: The command WITHOUT any priority prefix (ffmpeg and its args).
    :param timeout: Seconds before the process is killed and TimeoutExpired
        raised. None is accepted when a supplied progress watchdog is meant
        to be the ONLY lifetime bound, but a caller that can compute a
        per-file expected duration should always pass one alongside
        ``progress_path`` instead (see
        encode_watchdog.stream_copy_outer_timeout_seconds): the watchdog
        answers "did it stop writing", this answers "how long is this file
        allowed to take even if it never stops", and diagnosed incident
        t_260919_232308_921 is exactly the gap between the two -- the
        watchdog's own poll thread went silently unsupervised, and with
        timeout=None nothing else was left to bound the encode at all.
    :param runner: Clamp executor, injected by tests.
    :param progress_path: The output file, so a stalled encode is caught by not
        writing rather than by a wall-clock budget no constant can set well.
    :param stall_seconds: Quiet period on ``progress_path`` that counts as stuck.
    :param poll_seconds: Override for the supervisors' poll interval; tests use it.
    :param attended_check: Returns True/False/None for "does anything need this
        host". Injected as one seam so the launch decision and the mid-encode
        guard cannot disagree, and so tests supply a real callable rather than
        patching a module global. Defaults to the live Jellyfin + HID check.
    :param phase_recovery_factory: Bounded real hardware-tail observer by
        default; tests may inject a fixture or explicitly disable it with None.
    :param text: Decode stdout/stderr as text. Pass False when the caller needs
        ffmpeg's raw bytes off stdout.
    :param extend_while_progressing: t_260920_052208_901. When True, ``timeout``
        stops being a flat deadline calibrated from a throughput expectation
        (which Darwin's background I/O tier can invalidate by an unbounded
        factor -- see stream_copy_outer_timeout_seconds' docstring) and
        becomes an absolute ceiling behind a loop that keeps waiting as long
        as ``progress_path`` shows recent growth, at any speed. Requires
        ``progress_path``. Callers that can derive a per-file size-based
        bound (see encode_watchdog.stream_copy_outer_timeout_seconds) should
        pass this too, not rely on the bound alone.
        None (the default) means "True whenever ``progress_path`` is given":
        t_260921_182721_855 found the opt-in had reached only two callers,
        leaving every container_remux stream copy, audio_normalize,
        subtitle_embed, subtitle_pipeline, tv_export_subtitles and
        export-for-tv on a flat wall-clock bound that would SIGKILL a
        starved-but-writing encode (The Pitt S02E09, pid 57525, still
        writing after 10h against a 28800s bound). An attached watchdog now
        always decides "stuck" by progress; pass False only to test the
        flat-bound path itself.
    :param cancel_event: A run's cancellation signal (see
        ``lib.backfill_cancellation.RunCancellation``). When given and set,
        or when ``None`` and a caller upstream on this thread used
        ``cancel_scope`` to bind one, ends the wait early (SIGKILL, which
        reaches a clamp-SIGSTOPped child too) and raises ``EncodeCancelled``
        instead of running to completion or raising ``TimeoutExpired``. i261:
        a caller with no ``progress_path`` (e.g. media_probe's volumedetect)
        has no other way to bound a clamped, zero-progress child.
    :param progress_report: For an ffmpeg with no output file to watch and
        DENSE output (media_probe's null-muxer volumedetect): adds ffmpeg's
        own ``-progress`` report on local temp storage and watches the media
        position in it (lib.ffmpeg_progress_report), so a stall is judged by
        progress there too (t_260921_182721_855). Exclusive with
        ``progress_path``; never for sparse output such as a lone subtitle
        track, whose position can legitimately sit still for many minutes.
    :returns: CompletedProcess with captured stdout/stderr, text unless
        ``text=False``.
    """
    effective_cancel_event = (
        cancel_event if cancel_event is not None
        else bound_cancel_event())
    if effective_cancel_event is not None and effective_cancel_event.is_set():
        # A cancelled run starts nothing (t_260921_182721_855: pid 74819 was
        # launched 13s after cancellation fired, only to be killed again).
        raise EncodeCancelled(f"before launching {Path(argv[0]).name}")
    report_dir = None
    progress_stamp = None
    if progress_report:
        if progress_path is not None:
            raise ValueError("progress_report and progress_path are exclusive")
        report_dir = Path(tempfile.mkdtemp(prefix="ffmpeg-progress-"))
        progress_path = report_dir / "progress.txt"
        argv = with_progress_report(argv, progress_path)
        progress_stamp = progress_stamp_reader(progress_path)
    if timeout is None and progress_path is None:
        raise ValueError("timeout=None requires progress_path watchdog supervision")
    check = attended_check or _host_attended_check()
    requested_clamped = _clamp_at_launch(check)
    prefix = _priority_prefix(check, clamped=requested_clamped)
    taskpolicy_bin = resolve_tool("taskpolicy")
    effective_prefix_clamped = (
        len(prefix) >= 2 and prefix[0] == taskpolicy_bin and prefix[1] == "-b"
    )
    if extend_while_progressing is None:
        extend_while_progressing = progress_path is not None
    try:
        return _run_supervised(prefix + argv, timeout=timeout,
                               launched_clamped=effective_prefix_clamped, runner=runner,
                               progress_path=progress_path,
                               stall_seconds=stall_seconds, poll_seconds=poll_seconds,
                               attended_check=check,
                               extend_while_progressing=extend_while_progressing,
                               phase_recovery_factory=phase_recovery_factory,
                               text=text, cancel_event=effective_cancel_event,
                               progress_stamp=progress_stamp)
    finally:
        if report_dir is not None:
            _remove_progress_report(report_dir)


def _remove_progress_report(report_dir: Path) -> None:
    """Delete a run's -progress report directory, logging (never raising) a failure.

    The report is a few KB on local temp storage; failing to delete it must
    not turn a finished encode into an error, but it is still logged with
    its cause rather than dropped.
    """
    try:
        shutil.rmtree(report_dir)
    except OSError as exc:
        log.warning("could not remove ffmpeg progress report %s: %s", report_dir, exc)
