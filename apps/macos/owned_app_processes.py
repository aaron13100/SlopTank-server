# SlopTank modification notice: added or changed by SlopTank on 2026-11-12.
"""Control and verify the launcher-owned app and bundled server processes."""

from __future__ import annotations

import os
import platform
import signal
import socket
import subprocess
import time
from pathlib import Path
from typing import Any, Callable

from smoke_deadline import Deadline, POLL_SECONDS, SmokeError, run
from lib.load_scaled_timeout import load_scaled_timeout


EXITING_WAIT_SECONDS = 5


def server_child(app_pid: int, app_bundle: Path, profile_data: Path, expected_rid: str,
                 deadline: Deadline) -> dict[str, Any]:
    """Identify and verify the launcher-owned server child and native runtime."""
    while deadline.remaining() > 0:
        children = run(["/usr/bin/pgrep", "-P", str(app_pid)], "Inspect launcher children", deadline, timeout=5, check=False)
        if children.returncode not in (0, 1):
            raise SmokeError(f"Could not inspect launcher PID {app_pid} children: {children.stderr[-1000:]}")
        for line in children.stdout.splitlines():
            pid_text = line.strip()
            if not pid_text.isdigit():
                continue
            pid = int(pid_text)
            command = run(["/bin/ps", "-ww", "-p", pid_text, "-o", "command="], "Inspect bundled server child", deadline, timeout=5)
            cmd = command.stdout.strip()
            runtime = None
            for rid in ("osx-x64", "osx-arm64"):
                executable = app_bundle / "Contents/Resources/Runtimes" / rid / "Server/jellyfin"
                if str(executable) in cmd:
                    runtime = rid
                    break
            if runtime and str(profile_data) in cmd and "--datadir" in cmd:
                if runtime != expected_rid:
                    raise SmokeError(f"Native {platform.machine()} app launched {runtime} server, expected {expected_rid}.")
                return {"pid": pid, "ppid": app_pid, "runtime_id": runtime,
                        "executable": str(app_bundle / "Contents/Resources/Runtimes" / runtime / "Server/jellyfin")}
        time.sleep(min(POLL_SECONDS, max(0, deadline.remaining())))
    raise SmokeError(f"Could not identify the expected {expected_rid} server child of launcher PID {app_pid}.")


def quit_app(app: subprocess.Popen[bytes], work: Path, port: int, deadline: Deadline) -> None:
    """Request normal PID-scoped app Quit and verify its server port closes."""
    script = work / "terminate-owned-app.js"
    script.write_text(
        "ObjC.import('AppKit');\n"
        "class QuitFailure extends Error { constructor(code, message) { super(message); "
        "this.name = 'QuitFailure'; this.code = code; this.hint = 'Inspect the owned app log.'; } }\n"
        f"var app = $.NSRunningApplication.runningApplicationWithProcessIdentifier({app.pid});\n"
        "if (!app || ObjC.unwrap(app.isTerminated)) throw new QuitFailure('app_missing', 'Owned SlopTank app is not running.');\n"
        "var accepted = app.terminate;\n"
        "if (!ObjC.unwrap(accepted)) throw new QuitFailure('quit_refused', 'NSRunningApplication refused normal Quit.');\n",
        encoding="utf-8",
    )
    run(["/usr/bin/osascript", "-l", "JavaScript", str(script)],
        f"Request PID-specific AppKit Quit for {app.pid}", deadline, timeout=10)
    try:
        status = app.wait(timeout=deadline.timeout(60, "native app normal Quit"))
    except subprocess.TimeoutExpired as error:
        raise SmokeError(f"Native app PID {app.pid} did not exit after normal AppKit Quit.") from error
    if status != 0:
        raise SmokeError(f"Native app exited {status} after normal Quit.")
    while deadline.remaining() > 0:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=min(0.25, deadline.timeout(1, "verify server shutdown"))):
                pass
        except ConnectionRefusedError:
            return
        except OSError as error:
            raise SmokeError(f"Could not verify server port {port} closed: {error}") from error
        time.sleep(min(POLL_SECONDS, max(0, deadline.remaining())))
    raise SmokeError(f"Server port {port} remained open after normal app Quit.")


def classify_owned_process(state: str, command: str, executable: str, profile_data: Path) -> str:
    """Classify the `ps` row of the recorded server as running, exiting or exited."""
    if state.startswith("Z"):
        return "exited"
    if "E" in state and command == f"({Path(executable).name})":
        return "exiting"
    if not command.startswith(executable + " ") or "--datadir " + str(profile_data) not in command:
        raise SmokeError(f"Owned server no longer matches its executable and profile: {state!r} {command!r}")
    return "running"


def owned_server_status(child: dict[str, Any], profile_data: Path, deadline: Deadline) -> str:
    """Report the recorded server PID as running, exiting, exited or gone."""
    pid = str(child["pid"])
    result = run(["/bin/ps", "-ww", "-p", pid, "-o", "stat=", "-o", "command="],
                 "Inspect owned server before cleanup", deadline, timeout=5, check=False)
    if result.returncode == 1 and not result.stdout.strip():
        try:
            os.kill(int(pid), 0)
        except ProcessLookupError:
            return "gone"
        raise SmokeError(f"Could not inspect still-present server PID {pid}: {result.stderr!r}")
    if result.returncode:
        raise SmokeError(f"Could not inspect owned server PID {pid}: {result.stderr!r}")
    parts = result.stdout.strip().split(None, 1)
    if not parts:
        raise SmokeError(f"No process state returned for owned server PID {pid}.")
    return classify_owned_process(parts[0], parts[1] if len(parts) == 2 else "", str(child["executable"]), profile_data)


def owned_server_alive(child: dict[str, Any], profile_data: Path, deadline: Deadline,
                       *, exit_wait_seconds: float = EXITING_WAIT_SECONDS,
                       status: Callable[[dict[str, Any], Path, Deadline], str] = owned_server_status) -> bool:
    """Wait out a caught mid-exit state; never mistake another process for the server."""
    wait_until = min(deadline.at, time.monotonic() + load_scaled_timeout(exit_wait_seconds))
    while True:
        current = status(child, profile_data, deadline)
        if current == "running":
            return True
        if current != "exiting":
            return False
        if time.monotonic() >= wait_until:
            raise SmokeError(f"Owned server PID {child['pid']} is still exiting after {exit_wait_seconds}s.")
        time.sleep(min(POLL_SECONDS, max(0, wait_until - time.monotonic())))


def bundle_processes(copied_app: Path, deadline: Deadline) -> list[dict[str, Any]]:
    """List live processes executing from the owned app copy, regardless of ancestry."""
    listing = run(["/bin/ps", "-axww", "-o", "pid=", "-o", "stat=", "-o", "command="],
                  "List processes for owned bundle cleanup", deadline, timeout=10)
    prefix = str(copied_app) + "/"
    found = []
    for row in listing.stdout.splitlines():
        parts = row.strip().split(None, 2)
        if len(parts) < 3 or not parts[0].isdigit() or parts[1].startswith("Z"):
            continue
        if parts[2].startswith(prefix):
            found.append({"pid": int(parts[0]), "state": parts[1], "command": parts[2][:300]})
    return found


def stop_owned_processes(app: subprocess.Popen[bytes] | None, child: dict[str, Any] | None,
                         data_path: Path | None, copied_app: Path | None,
                         receipt: dict[str, Any], deadline: Deadline) -> bool:
    """Stop owned processes and return true only after the app and bundle are clear."""
    cleanup = receipt.setdefault("cleanup", [])
    remaining: list[dict[str, Any]] = []
    grace = Deadline(60.0)
    try:
        for sig in (signal.SIGTERM, signal.SIGKILL):
            targets: dict[int, str] = {}
            if child is not None and data_path is not None:
                try:
                    if owned_server_alive(child, data_path, grace):
                        targets[int(child["pid"])] = "server_pid"
                except (OSError, SmokeError) as error:
                    receipt["cleanup_errors"].append(f"Could not inspect recorded server PID {child['pid']}: {error}")
            if copied_app is not None:
                for row in bundle_processes(copied_app, grace):
                    targets.setdefault(row["pid"], "bundle_pid")
            if app is not None and app.poll() is None:
                targets[app.pid] = "app_pid"
            for pid, role in targets.items():
                try:
                    os.kill(pid, sig)
                    cleanup.append({role: pid, "signal": sig.name})
                except ProcessLookupError:
                    cleanup.append({role: pid, "already_stopped": True})
                except OSError as error:
                    receipt["cleanup_errors"].append(f"Could not signal {role} {pid}: {error}")
            end = min(grace.at, time.monotonic() + 5)
            while True:
                remaining = bundle_processes(copied_app, grace) if copied_app is not None else []
                if not remaining and (app is None or app.poll() is not None):
                    return True
                if time.monotonic() >= end:
                    break
                time.sleep(min(0.2, max(0, end - time.monotonic())))
        receipt["cleanup_errors"].append(
            f"Owned processes still running after SIGKILL: app_running={app is not None and app.poll() is None}, "
            f"bundle={remaining!r}")
        return False
    except Exception as error:
        receipt["cleanup_errors"].append(f"Could not verify owned process cleanup: {error}")
        return False
    finally:
        if app is not None:
            try:
                app.poll()  # Reap the launcher even when it has already become a zombie.
            except OSError as error:
                receipt["cleanup_errors"].append(f"Could not reap app PID {app.pid}: {error}")


def launch_app(executable: Path, environment: dict[str, str], log_path: Path) -> subprocess.Popen[bytes]:
    """Start the app executable with output appended to its owned log."""
    with log_path.open("ab", buffering=0) as log:
        return subprocess.Popen([str(executable)], cwd=str(executable.parent), env=environment,
                                stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                close_fds=True, start_new_session=True)
