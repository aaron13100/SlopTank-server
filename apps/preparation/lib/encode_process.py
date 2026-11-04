# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Race-free process launch mechanics for supervised encodes."""

from __future__ import annotations

import logging
import os
import signal
import subprocess
import sys
import time

from lib.encode_attendance import _default_runner, resolve_tool


def terminate_and_reap(proc) -> None:
    """Kill and reap a child whose launch policy could not be established."""
    try:
        proc.kill()
    except (OSError, ProcessLookupError):  # allow-silent-catch: cleanup is idempotent; wait below proves reap state
        pass
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        # A Popen child that ignores SIGKILL is exceptional, but never leave a
        # suspended child behind merely because the first wait timed out.
        try:
            proc.kill()
        except (OSError, ProcessLookupError):  # allow-silent-catch: cleanup retry is idempotent; final wait still propagates
            pass
        proc.wait()


def _policy_succeeded(argv: list[str], runner=None, logger=None) -> bool:
    """Run one taskpolicy operation, treating every error as failure."""
    try:
        return bool((runner or _default_runner)(argv))
    except Exception as exc:  # noqa: BLE001 - launch policy must fail closed
        (logger or logging.getLogger("encode_priority")).warning(
            "taskpolicy %s raised (%s: %s)",
            " ".join(argv[1:]), type(exc).__name__, exc)
        return False


def launch_process(cmd: list[str], *, launched_clamped: bool,
                   policy_runner=None, text: bool = True,
                   logger=None, popen_factory=None, taskpolicy_bin=None,
                   platform_name=None, tool_exists=None, kill=None,
                   wait_stopped=None):
    """Start one encode and return ``(process, effective_clamped)``.

    A confirmed-unclamped launch on Darwin must actively escape an ambient
    ``PRIO_DARWIN_BG`` inherited from the long-lived backfill process. The
    child is therefore created stopped with ``taskpolicy -s``; ``-B`` is
    applied while it cannot execute, and only then is SIGCONT sent. If the
    release fails, the still-stopped child is positively clamped before it is
    resumed. Failure to establish either state kills and reaps the child.

    Every policy call waits until the child has actually stopped
    (``wait_stopped``): until then the pid is still ``taskpolicy`` itself,
    mid posix_spawn, and a ``-b -p`` landing there was measured to be
    dropped by the spawn (the child came up at priority 31, t_260921_182721_855).

    ``policy_runner`` is a narrow test seam. It is deliberately separate from
    the guard runner because launching an idle encode must not make the guard's
    taskpolicy call history look like a mid-run clamp.
    """
    log = logger or logging.getLogger("encode_priority")
    popen_factory = popen_factory or subprocess.Popen
    taskpolicy_bin = taskpolicy_bin or resolve_tool("taskpolicy")
    platform_name = platform_name or sys.platform
    tool_exists = tool_exists or os.path.exists
    kill = kill or os.kill
    wait_stopped = wait_stopped or _wait_until_stopped
    # t_260921_182721_855: a program started through the ``taskpolicy -b``
    # prefix ignores a later ``taskpolicy -B -p`` on this host (rc 0, still at
    # priority 4), so the guard's "released the clamp" was a silent no-op and
    # every launch-clamped stream copy stayed starved for its whole life
    # (pid 57525: 2.19 of 3.28 GB in ten hours). ``-b -p`` applied to the
    # stopped child is reversible and is in force before its first
    # instruction, so the prefix is translated into that instead.
    clamp_prefixed = launched_clamped and cmd[:2] == [taskpolicy_bin, "-b"]
    suspended_launch = (
        platform_name == "darwin"
        and tool_exists(taskpolicy_bin)
        and (clamp_prefixed or not launched_clamped)
    )
    if not suspended_launch:
        proc = popen_factory(cmd, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, text=text)
        return proc, launched_clamped

    program = cmd[2:] if clamp_prefixed else cmd
    proc = popen_factory([taskpolicy_bin, "-s", *program], stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=text)
    pid = proc.pid
    try:
        stopped = wait_stopped(proc)
    except BaseException:
        # e.g. ps could not be forked under resource exhaustion: never leave
        # a suspended child behind holding its pipes.
        terminate_and_reap(proc)
        raise
    if not stopped:
        # It exited before it could stop (taskpolicy could not spawn the
        # program); the caller's communicate() reports that exit as-is.
        return proc, launched_clamped
    if not launched_clamped:
        release_argv = [taskpolicy_bin, "-B", "-p", str(pid)]
        if _policy_succeeded(release_argv, policy_runner, log):
            _resume(proc, kill, "verified-unclamped")
            log.info("released suspended encode pid %d before execution", pid)
            return proc, False

    clamp_argv = [taskpolicy_bin, "-b", "-p", str(pid)]
    if _policy_succeeded(clamp_argv, policy_runner, log):
        _resume(proc, kill, "clamped" if launched_clamped else "fallback-clamped")
        if launched_clamped:
            log.info("clamped suspended encode pid %d before execution "
                     "(reversible)", pid)
        else:
            log.warning("could not release suspended encode pid %d; resumed "
                        "after confirmed clamp", pid)
        return proc, True

    terminate_and_reap(proc)
    raise RuntimeError(
        f"could not establish either unclamped or clamped launch policy for pid {pid}")


def _resume(proc, kill, state: str) -> None:
    """SIGCONT a stopped child whose launch policy is in force, or kill it."""
    try:
        kill(proc.pid, signal.SIGCONT)
    except OSError as exc:
        terminate_and_reap(proc)
        raise RuntimeError(
            f"could not resume {state} encode pid {proc.pid}: {exc}") from exc


def _wait_until_stopped(proc, *, poll_seconds: float = 0.01) -> bool:
    """Block until a ``taskpolicy -s`` child is stopped; False if it exited.

    ``taskpolicy -s`` suspends the task rather than signal-stopping it, so
    waitpid(WUNTRACED) never reports it (measured: (0, 0) while ps already
    shows T); the process state ps reports is the observable. Unbounded on
    purpose: the child can only become stopped or exit, and until then it
    is nothing but taskpolicy. An exit is reaped through ``proc.poll()`` so
    its status stays on ``proc`` for the caller.
    """
    while proc.poll() is None:
        state = subprocess.run(
            [resolve_tool("ps"), "-o", "stat=", "-p", str(proc.pid)],
            capture_output=True, text=True).stdout.strip()
        if state.startswith("T"):
            return True
        time.sleep(poll_seconds)
    return False
