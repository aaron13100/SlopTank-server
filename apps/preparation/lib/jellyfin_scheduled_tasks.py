# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Polling and sequencing for Jellyfin scheduled tasks.

The transport and playback-session reader are injected so this module stays
independent of credential loading and low-level HTTP implementation.
"""

from __future__ import annotations

import json
import logging
import time
from dataclasses import dataclass
from typing import Any, Callable, Optional

# Preserve the logger category used before this engine was split out.
log = logging.getLogger("jellyfin_client")

POST_CONVERSION_ANALYSIS_KEYS = (
    "RefreshTrickplayImages",
    "KeyframeExtraction",
    "TaskExtractMediaSegments",
    "RefreshChapterImages",
)


@dataclass(frozen=True)
class ScheduledTaskRun:
    """Outcome of starting and waiting for one Jellyfin scheduled task."""

    completed: bool
    reason: str
    state: str = "unknown"
    progress: Optional[float] = None
    started: bool = False

    def __bool__(self) -> bool:
        return self.completed


# A task is late when progress stops, not merely when runtime is long.
DEFAULT_STALL_SECONDS = 1800
# Backstop for a task whose reported progress somehow changes forever.
ABSOLUTE_TASK_CAP_SECONDS = 86400


def _scheduled_task(base: str, token: str, key: str,
                    request: Callable[..., Any]) -> Optional[dict]:
    """Return the whole TaskInfo for its stable key, or None."""
    out = request("GET", f"{base}/ScheduledTasks", token)
    if not out:
        return None
    try:
        tasks = json.loads(out)
    except (ValueError, TypeError) as exc:
        log.warning("could not parse %s/ScheduledTasks: %s", base, exc)
        return None
    return next((task for task in tasks if task.get("Key") == key), None)


def _last_end_time(task: dict) -> Optional[str]:
    """EndTimeUtc of the previous run, or None if it never ran."""
    return (task.get("LastExecutionResult") or {}).get("EndTimeUtc")


def _wait_for_scheduled_task(base: str, token: str, key: str, task_id: str,
                             end_time_before: Optional[str],
                             request: Callable[..., Any], poll_s: int,
                             stall_s: int,
                             clock: Callable[[], float],
                             overall_deadline: Optional[float] = None,
                             ) -> ScheduledTaskRun:
    """Poll one started task until completion, stall, or the liveness cap.

    ``overall_deadline`` (a monotonic-clock timestamp) bounds the CALLER's
    whole tail, not just this task (i261, finding section 1): these are
    Jellyfin's own scheduled tasks, so abandoning the wait on expiry is
    always safe -- it will run them on its own schedule -- while waiting
    unbounded let released backfill instances live for days behind a
    starved-but-progressing scan.
    """
    started = clock()
    last_change = started
    last_progress: Optional[float] = None
    state = "unknown"
    observed_running = False
    while clock() - started < ABSOLUTE_TASK_CAP_SECONDS:
        if overall_deadline is not None and clock() >= overall_deadline:
            return ScheduledTaskRun(
                False,
                f"{key}: tail budget exhausted; Jellyfin will run it on "
                f"its own schedule",
                state, last_progress, started=True)
        time.sleep(poll_s)
        out = request("GET", f"{base}/ScheduledTasks/{task_id}", token)
        if out:
            try:
                body = json.loads(out)
            except (ValueError, TypeError) as exc:
                log.warning("could not parse state of %s: %s", key, exc)
                body = None
            if body is not None:
                state = body.get("State") or "unknown"
                progress = body.get("CurrentProgressPercentage")
                if state != "Idle":
                    observed_running = True
                if progress != last_progress:
                    last_progress = progress
                    last_change = clock()
                if state == "Idle" and (
                        observed_running
                        or _last_end_time(body) != end_time_before):
                    return ScheduledTaskRun(
                        True, "ok", state, last_progress, started=True)
        if clock() - last_change > stall_s:
            return ScheduledTaskRun(
                False,
                f"{key} stopped advancing: state {state}, progress "
                f"{last_progress}, unchanged for {stall_s}s",
                state, last_progress, started=True)
    return ScheduledTaskRun(
        False,
        f"{key} still {state} after the {ABSOLUTE_TASK_CAP_SECONDS}s liveness cap",
        state, last_progress, started=True)


def _run_scheduled_task(base: str, token: str, key: str,
                        request: Callable[..., Any], poll_s: int,
                        stall_s: int,
                        clock: Callable[[], float],
                        overall_deadline: Optional[float] = None,
                        ) -> ScheduledTaskRun:
    """Start the task by stable key and wait for proven completion."""
    task = _scheduled_task(base, token, key, request)
    if not task:
        return ScheduledTaskRun(False, f"no scheduled task with key {key!r}")
    task_id = task.get("Id")
    end_time_before = _last_end_time(task)
    if request("POST", f"{base}/ScheduledTasks/Running/{task_id}", token) is None:
        return ScheduledTaskRun(False, f"server refused to start {key}")
    return _wait_for_scheduled_task(
        base, token, key, task_id, end_time_before, request, poll_s, stall_s,
        clock, overall_deadline=overall_deadline)


def run_scheduled_task(base: str, token: str, key: str, *, request,
                       stall_s: int = DEFAULT_STALL_SECONDS, poll_s: int = 6,
                       clock: Callable[[], float] = time.monotonic,
                       overall_deadline: Optional[float] = None,
                       ) -> ScheduledTaskRun:
    """Trigger a scheduled task by stable key and wait until it finishes.

    ``overall_deadline`` is the caller's monotonic tail budget; see
    _wait_for_scheduled_task."""
    run = _run_scheduled_task(base, token, key, request, poll_s, stall_s, clock,
                              overall_deadline=overall_deadline)
    if not run:
        log.warning("scheduled task did not complete: %s", run.reason)
    return run


def run_library_scan(base: str, token: str, *, request,
                     stall_s: int = DEFAULT_STALL_SECONDS, poll_s: int = 6,
                     clock: Callable[[], float] = time.monotonic,
                     overall_deadline: Optional[float] = None,
                     ) -> ScheduledTaskRun:
    """Trigger the library scan task and block until it finishes.

    ``overall_deadline`` is the caller's monotonic tail budget; see
    _wait_for_scheduled_task."""
    return run_scheduled_task(
        base, token, "RefreshLibrary", request=request,
        stall_s=stall_s, poll_s=poll_s, clock=clock,
        overall_deadline=overall_deadline)


def _analysis_playback_stop_reason(
        base: str, token: str, stale_after_seconds: int, *, request,
        active_sessions) -> Optional[str]:
    """Return why analysis must stop, or None only for confirmed idleness."""
    sessions = active_sessions(
        base, token, stale_after_seconds, request=request)
    if sessions is None:
        return "skipped: playback state unknown"
    if sessions:
        return "skipped: someone is watching"
    return None


def _analysis_task_result(run: ScheduledTaskRun) -> tuple[str, Optional[str]]:
    """Translate one task run into its outcome and optional safety stop."""
    outcome = "ok" if run else f"failed: {run.reason}"
    stop_reason = None
    if not run and run.started and run.state != "Idle":
        stop_reason = (
            "skipped: previous task may still be running: "
            f"{run.reason}")
    return outcome, stop_reason


def run_post_conversion_analysis(
        base: str, token: str, *, request, active_sessions,
        stall_s: int = DEFAULT_STALL_SECONDS, poll_s: int = 6,
        stale_after_seconds: int = 180,
        clock: Callable[[], float] = time.monotonic,
        overall_deadline: Optional[float] = None) -> dict[str, str]:
    """Run readiness tasks in order, rechecking playback between each.

    ``overall_deadline`` is the caller's monotonic tail budget (i261):
    once it elapses, no further task is STARTED and any in-flight wait
    returns the budget-exhausted reason instead of blocking; Jellyfin runs
    these on its own schedule regardless."""
    outcomes: dict[str, str] = {}
    stop_reason: Optional[str] = None
    for key in POST_CONVERSION_ANALYSIS_KEYS:
        if (overall_deadline is not None and clock() >= overall_deadline):
            outcomes[key] = (
                f"{key}: tail budget exhausted; Jellyfin will run it on "
                f"its own schedule")
            continue
        if stop_reason is None:
            stop_reason = _analysis_playback_stop_reason(
                base, token, stale_after_seconds, request=request,
                active_sessions=active_sessions)
            if stop_reason:
                log.info("post-conversion analysis stopping: %s", stop_reason)
        if stop_reason is not None:
            outcomes[key] = stop_reason
            continue
        run = _run_scheduled_task(
            base, token, key, request, poll_s, stall_s, clock,
            overall_deadline=overall_deadline)
        outcomes[key], task_stop_reason = _analysis_task_result(run)
        if not run:
            log.warning("post-conversion analysis: %s", run.reason)
        if task_stop_reason:
            stop_reason = task_stop_reason
    return outcomes
