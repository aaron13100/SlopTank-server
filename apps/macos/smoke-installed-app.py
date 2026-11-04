#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Run the isolated installed-app acceptance journey against a SlopTank DMG.

Usage: python3 apps/macos/smoke-installed-app.py --dmg SlopTank.dmg \
    --work-dir "$(mktemp -d "${TMPDIR:-/private/tmp}/sloptank-smoke.XXXXXX")"

The work directory retains logs, generated media, profile and a JSON receipt.
Only the copied app is removed. The mounted image is always detached.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import platform
import plistlib
import signal
import socket
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any, Callable


# The same portable helper lives under tools in the operations checkout.
preparation_source = Path(__file__).resolve().parents[1] / "preparation"
if not preparation_source.is_dir():
    preparation_source = Path(__file__).resolve().parents[2] / "tools"
sys.path.insert(0, str(preparation_source))
from lib.load_scaled_timeout import load_scaled_timeout


SYSTEM_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"
APP_NAME = "SlopTank.app"
PROFILE_RELATIVE = Path("Library/Application Support/SlopTank")
ADMIN_NAME = "smoke-admin"
ADMIN_PASSWORD = "Smoke-only-password-4917!"
MEDIA_NAME = "SlopTank Installed App Smoke.mp4"
POLL_SECONDS = 0.5
EXITING_WAIT_SECONDS = 5
CLEANUP_RESERVE_SECONDS = 75


class SmokeError(RuntimeError):
    """An acceptance step failed with its observed diagnostic."""


class Deadline:
    """One wall-clock deadline shared by all commands and HTTP requests."""

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


def write_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def make_profile(home: Path, port: int) -> dict[str, Path | str]:
    """Create the launcher's documented ready profile and matching markers."""
    support = home / PROFILE_RELATIVE
    profile = support / "ServerProfile"
    authority = support / "PermalinkAuthority"
    config = profile / "Configuration"
    cache = profile / "Cache"
    for folder in (support, profile, authority, config, cache):
        folder.mkdir(mode=0o700, parents=True, exist_ok=True)
        folder.chmod(0o700)
    profile_id = str(uuid.uuid4())
    write_json(support / "profile.json", {
        "schemaVersion": 1,
        "profileID": profile_id,
        "selectedRootPath": str(profile),
        "startupCompleted": True,
        "initializationState": "ready",
    })
    marker = f"SlopTank profile version 1\nprofileId={profile_id}\n"
    for path in (profile / ".sloptank-profile", authority / ".sloptank-authority-profile"):
        path.write_text(marker, encoding="utf-8")
        path.chmod(0o600)
    (config / "network.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<NetworkConfiguration>\n'
        f"  <InternalHttpPort>{port}</InternalHttpPort>\n"
        f"  <PublicHttpPort>{port}</PublicHttpPort>\n"
        "  <AutoDiscovery>false</AutoDiscovery>\n"
        "</NetworkConfiguration>\n",
        encoding="utf-8",
    )
    return {"support": support, "profile": profile, "authority": authority,
            "config": config, "cache": cache, "profile_id": profile_id}


def api(
    base: str,
    method: str,
    path: str,
    deadline: Deadline,
    *,
    body: dict[str, Any] | None = None,
    token: str | None = None,
    timeout: float = 30,
) -> tuple[int, bytes, dict[str, str]]:
    headers = {
        "Accept": "application/json",
        "Authorization": ('MediaBrowser Client="SlopTank Installed App Smoke", Device="Smoke", '
                          'DeviceId="installed-app-smoke", Version="1.0"'),
    }
    payload = json.dumps(body).encode() if body is not None else None
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] += f', Token="{token}"'
    request = urllib.request.Request(base.rstrip("/") + path, data=payload, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=deadline.timeout(timeout, f"{method} {path}")) as response:
            return response.status, response.read(), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise SmokeError(f"{method} {path}: {error}") from error


def json_api(base: str, method: str, path: str, deadline: Deadline, *,
             body: dict[str, Any] | None = None, token: str | None = None,
             timeout: float = 30) -> dict[str, Any]:
    status, data, _ = api(base, method, path, deadline, body=body, token=token, timeout=timeout)
    if not 200 <= status < 300:
        raise SmokeError(f"{method} {path}: HTTP {status}: {data[:2000].decode(errors='replace')}")
    try:
        value = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SmokeError(f"{method} {path}: invalid JSON response {data[:500]!r}") from error
    if not isinstance(value, dict):
        raise SmokeError(f"{method} {path}: expected JSON object, got {type(value).__name__}")
    return value


def public_info(base: str, deadline: Deadline, app: subprocess.Popen[bytes]) -> dict[str, Any]:
    last = "no response"
    while deadline.remaining() > 0:
        if app.poll() is not None:
            raise SmokeError(f"Native app exited {app.returncode} before its server became ready.")
        try:
            value = json_api(base, "GET", "/System/Info/Public", deadline, timeout=30)
            if value.get("Id"):
                return value
            last = f"PublicInfo has no server Id: {value!r}"
        except SmokeError as error:
            last = str(error)
        time.sleep(min(POLL_SECONDS, max(0, deadline.remaining())))
    raise SmokeError(f"Server did not become ready before the deadline: {last}")


def authenticate(base: str, deadline: Deadline) -> tuple[str, str]:
    value = json_api(base, "POST", "/Users/AuthenticateByName", deadline,
                     body={"Username": ADMIN_NAME, "Pw": ADMIN_PASSWORD})
    user = value.get("User")
    if not value.get("AccessToken") or not isinstance(user, dict) or not user.get("Id"):
        raise SmokeError(f"Authentication omitted the token or user ID: {value!r}")
    return str(value["AccessToken"]), str(user["Id"])


def wait_movie(base: str, token: str, media: Path, deadline: Deadline) -> dict[str, Any]:
    query = urllib.parse.urlencode({"Recursive": "true", "IncludeItemTypes": "Movie", "Fields": "Path"})
    last: Any = []
    while deadline.remaining() > 0:
        response = json_api(base, "GET", "/Items?" + query, deadline, token=token)
        last = response.get("Items")
        if not isinstance(last, list):
            raise SmokeError(f"Movie listing omitted Items: {response!r}")
        for item in last:
            if Path(str(item.get("Path", ""))).resolve() == media.resolve() and item.get("Id"):
                return item
        time.sleep(min(POLL_SECONDS, max(0, deadline.remaining())))
    raise SmokeError(f"Generated Movie was not found. Last listing: {last[:10]!r}")


def direct_range(base: str, token: str, item_id: str, media: Path, deadline: Deadline) -> dict[str, Any]:
    count = 1024
    path = f"/Videos/{urllib.parse.quote(item_id, safe='')}/stream.mp4?Static=true"
    request = urllib.request.Request(base.rstrip("/") + path, headers={
        "Range": f"bytes=0-{count - 1}",
        "Authorization": f'MediaBrowser Client="Smoke", Device="Smoke", DeviceId="smoke", Version="1", Token="{token}"',
    })
    try:
        with urllib.request.urlopen(request, timeout=deadline.timeout(15, "direct stream Range GET")) as response:
            status, body, headers = response.status, response.read(count + 1), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        raise SmokeError(f"Direct stream returned HTTP {error.code}: {error.read(2000).decode(errors='replace')}") from error
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise SmokeError(f"Direct stream Range GET failed: {error}") from error
    expected = media.read_bytes()[:count]
    expected_range = f"bytes 0-{len(expected) - 1}/{media.stat().st_size}"
    content_range = headers.get("Content-Range") or headers.get("content-range")
    if status != 206 or content_range != expected_range or body != expected:
        raise SmokeError(f"Range mismatch: HTTP {status}, Content-Range {content_range!r}, bytes={len(body)}")
    return {"status": status, "content_range": content_range, "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest()}


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


def log_tail(path: Path, limit: int = 20000) -> str:
    if not path.is_file():
        return ""
    with path.open("rb") as stream:
        stream.seek(max(0, path.stat().st_size - limit))
        return stream.read().decode("utf-8", errors="replace")


def classify_owned_process(state: str, command: str, executable: str, profile_data: Path) -> str:
    """Classify the `ps` row of the recorded server as "running", "exiting" or "exited".

    Any other program is refused with SmokeError, so cleanup never signals a process that is not
    the recorded server. A zombie has stopped. A process the kernel is tearing down (state flag E)
    no longer has its arguments: ps shows only its name in parentheses, for example `(jellyfin)`.
    """
    if state.startswith("Z"):
        return "exited"
    if "E" in state and command == f"({Path(executable).name})":
        return "exiting"
    if not command.startswith(executable + " ") or "--datadir " + str(profile_data) not in command:
        raise SmokeError(f"Owned server no longer matches its executable and profile: {state!r} {command!r}")
    return "running"


def owned_server_status(child: dict[str, Any], profile_data: Path, deadline: Deadline) -> str:
    """Report the recorded server PID as "running", "exiting", "exited" or "gone"."""
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
    """True while the recorded server runs; one caught mid-exit is waited for, never taken for another process."""
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


def launch_app(executable: Path, environment: dict[str, str], log_path: Path) -> subprocess.Popen[bytes]:
    with log_path.open("ab", buffering=0) as log:
        return subprocess.Popen([str(executable)], cwd=str(executable.parent), env=environment,
                                stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                close_fds=True, start_new_session=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dmg", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=int, default=1200, help="One deadline for the full journey and cleanup; default 1200")
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("This installed-app smoke runs only on macOS.")
    if args.timeout_seconds < 300:
        parser.error("--timeout-seconds must be at least 300")
    dmg = args.dmg.expanduser().absolute()
    if not dmg.is_file() or dmg.is_symlink():
        parser.error(f"--dmg must name a regular file: {dmg}")
    work = args.work_dir.expanduser().absolute()
    if work.is_symlink():
        parser.error(f"--work-dir cannot be a symbolic link: {work}")
    work = work.resolve()
    if work == work.parent or not work.parent.is_dir():
        parser.error(f"--work-dir must be a new child of an existing scratch directory: {work}")
    if work.exists():
        if not work.is_dir() or any(work.iterdir()):
            parser.error(f"--work-dir must be a new or empty real directory: {work}")
    else:
        work.mkdir(mode=0o700)
    work.chmod(0o700)

    started = time.monotonic()
    deadline = Deadline(args.timeout_seconds)
    operations = Deadline(max(1, args.timeout_seconds - CLEANUP_RESERVE_SECONDS))
    operations.at = deadline.at - CLEANUP_RESERVE_SECONDS
    receipt_path = work / "smoke-installed-app.json"
    receipt: dict[str, Any] = {"schemaVersion": 1, "type": "sloptank-installed-app-smoke",
                               "status": "running", "started_at": dt.datetime.now(dt.timezone.utc).isoformat(),
                               "dmg": str(dmg), "work_dir": str(work), "timeout_seconds": args.timeout_seconds,
                               "machine": platform.machine(), "cleanup_errors": []}
    mounts = work / "mounts"
    apps = work / "Applications"
    home = work / "isolated-home"
    media_dir = work / "scratch-media/Movies"
    for folder in (mounts, apps, home, media_dir):
        folder.mkdir(mode=0o700, parents=True)
    mounted: Path | None = None
    device: str | None = None
    copied_app: Path | None = None
    process: subprocess.Popen[bytes] | None = None
    child: dict[str, Any] | None = None
    profile: dict[str, Path | str] | None = None
    media: Path | None = None
    result_code = 1
    owned_processes_stopped = True
    app_log = work / "native-app.log"
    server_log: Path | None = None
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith(("JELLYFIN_", "SLOPTANK_", "DOTNET_"))}
    environment.update({"PATH": SYSTEM_PATH, "HOME": str(home), "CFFIXED_USER_HOME": str(home)})

    def interrupted(signum: int, _frame: object) -> None:
        raise SmokeError(f"Smoke interrupted by {signal.Signals(signum).name}.")

    signal.signal(signal.SIGTERM, interrupted)

    try:
        attach = run(["/usr/bin/hdiutil", "attach", "-readonly", "-nobrowse", "-plist",
                      "-mountroot", str(mounts), str(dmg)], "Mount DMG read-only", operations, timeout=90)
        attached = plistlib.loads(attach.stdout.encode())
        entities = attached.get("system-entities", [])
        device = next((entry.get("dev-entry") for entry in entities if entry.get("dev-entry")), None)
        mount_entry = next((entry for entry in entities if entry.get("mount-point")), None)
        if not mount_entry or not device:
            raise SmokeError(f"hdiutil attach omitted a mounted volume or device: {attached!r}")
        mounted = Path(mount_entry["mount-point"]).resolve()
        if not mounted.is_relative_to(mounts.resolve()):
            raise SmokeError(f"Image mounted outside its owned directory: {mounted}")
        source_app = mounted / APP_NAME
        if not source_app.is_dir() or source_app.is_symlink():
            raise SmokeError(f"Mounted image has no regular {APP_NAME}: {source_app}")
        copied_app = apps / APP_NAME
        run(["/usr/bin/ditto", str(source_app), str(copied_app)], "Copy app to isolated Applications", operations, timeout=480)
        executable = copied_app / "Contents/MacOS/SlopTank"
        ffmpeg = copied_app / "Contents/Resources/ffmpeg/ffmpeg"
        if not executable.is_file() or not os.access(executable, os.X_OK):
            raise SmokeError(f"Installed bundle has no executable launcher: {executable}")
        if not ffmpeg.is_file() or not os.access(ffmpeg, os.X_OK):
            raise SmokeError(f"Installed bundle has no executable bundled ffmpeg: {ffmpeg}")

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("0.0.0.0", 0))
            port = int(probe.getsockname()[1])
        if port == 8096:
            raise SmokeError("OS selected the reserved default port 8096; rerun to select another port.")
        profile = make_profile(home, port)
        environment["TMPDIR"] = str(profile["cache"])
        profile_path = Path(profile["profile"])
        data_path = profile_path / "Data"
        server_log = profile_path / "Logs/server.log"
        base = f"http://127.0.0.1:{port}"
        expected_rid = {"x86_64": "osx-x64", "arm64": "osx-arm64"}.get(platform.machine())
        if expected_rid is None:
            raise SmokeError(f"Unsupported native runtime architecture: {platform.machine()!r}")
        receipt["isolation"] = {"profile": str(profile_path), "authority": str(profile["authority"]),
                                "network_xml": str(Path(profile["config"]) / "network.xml"),
                                "port": port, "system_path": SYSTEM_PATH,
                                "removed_environment_prefixes": ["JELLYFIN_", "SLOPTANK_", "DOTNET_"],
                                "profile_id": profile["profile_id"], "runtime_expected": expected_rid}

        runtime = copied_app / "Contents/Resources/Runtimes" / expected_rid
        converter = runtime / "Server/svg-rasterizer/svg-rasterizer"
        worker = runtime / "Preparation/sloptank-prepare"
        svg = work / "helper-probe.svg"
        png = work / "helper-probe.png"
        svg.write_text('<svg xmlns="http://www.w3.org/2000/svg" width="4" height="3">'
                       '<rect width="4" height="3" fill="red"/></svg>', encoding="utf-8")
        run([str(converter), str(svg), str(png)], "Run installed SVG converter", operations,
            timeout=90, env=environment)
        header = png.read_bytes()[:24]
        expected_header = b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x04\x00\x00\x00\x03"
        if header != expected_header:
            raise SmokeError(f"Installed SVG converter produced an invalid 4x3 PNG header: {header!r}")
        worker_result = run([str(worker)], "Probe installed preparation worker", operations,
                            timeout=90, check=False, env=environment)
        try:
            event = json.loads(worker_result.stdout)
        except ValueError as error:
            raise SmokeError(f"Preparation worker returned invalid JSON: {worker_result.stdout!r}; "
                             f"stderr: {worker_result.stderr!r}") from error
        if (worker_result.returncode != 2 or not isinstance(event, dict)
                or event.get("type") != "fatal" or not isinstance(event.get("error"), dict)
                or event["error"].get("code") != "ipc-unexpected-eof"):
            raise SmokeError(f"Unexpected preparation worker probe: exit={worker_result.returncode}, "
                             f"event={event!r}, stderr={worker_result.stderr!r}")
        receipt["native_helpers"] = {"runtime_id": expected_rid,
                                     "svg": {"executable": str(converter), "width": 4, "height": 3},
                                     "preparation": {"executable": str(worker), "exit": worker_result.returncode,
                                                     "type": event["type"], "error_code": event["error"]["code"]}}

        media = media_dir / MEDIA_NAME
        run([str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=c=black:s=320x180:r=24",
             "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
             "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
             "-movflags", "+faststart", "-shortest", str(media)],
            "Generate H.264/AAC MP4 with bundled ffmpeg", operations, timeout=90)
        receipt["media"] = {"path": str(media), "size_bytes": media.stat().st_size,
                            "sha256": hashlib.sha256(media.read_bytes()).hexdigest()}

        process = launch_app(executable, environment, app_log)
        receipt["app_pid"] = process.pid
        child = server_child(process.pid, copied_app, data_path, expected_rid, operations)
        receipt["server_process"] = child
        info = public_info(base, operations, process)
        if info.get("StartupWizardCompleted") is not False:
            raise SmokeError(f"Fresh fixture should require setup, PublicInfo said: {info!r}")
        # The wizard initializes its first user through this endpoint before updating it.
        initial_user = json_api(base, "GET", "/Startup/User", operations, timeout=60)
        if not isinstance(initial_user.get("Name"), str) or not initial_user["Name"]:
            raise SmokeError(f"Startup did not initialize its first user: {initial_user!r}")
        receipt["initial_user_initialized"] = True
        for path, body in (
            ("/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}),
            ("/Startup/User", {"Name": ADMIN_NAME, "Password": ADMIN_PASSWORD}),
            ("/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False}),
            ("/Startup/Complete", {}),
        ):
            status, body_data, _ = api(base, "POST", path, operations, body=body, timeout=60)
            if not 200 <= status < 300:
                raise SmokeError(f"Setup {path}: HTTP {status}: {body_data[:2000].decode(errors='replace')}")
        token, user_id = authenticate(base, operations)
        if json_api(base, "GET", "/System/Info/Public", operations).get("StartupWizardCompleted") is not True:
            raise SmokeError("Startup wizard did not report completion after setup.")
        query = urllib.parse.urlencode({"name": "Smoke Movies", "collectionType": "movies",
                                        "paths": str(media_dir), "refreshLibrary": "true"})
        status, body, _ = api(base, "POST", "/Library/VirtualFolders?" + query, operations, body={}, token=token, timeout=60)
        if not 200 <= status < 300:
            raise SmokeError(f"Adding scratch Movies library: HTTP {status}: {body[:2000].decode(errors='replace')}")
        status, body, _ = api(base, "POST", "/Library/Refresh", operations, token=token, timeout=20)
        if not 200 <= status < 300:
            raise SmokeError(f"Refreshing scratch Movies library: HTTP {status}: {body[:2000].decode(errors='replace')}")
        item = wait_movie(base, token, media, operations)
        stream = direct_range(base, token, str(item["Id"]), media, operations)
        plugin_lines = [line for line in server_log.read_text(errors="replace").splitlines() if "Intro Skipper" in line]
        plugin_loaded = any("Loaded plugin: Intro Skipper" in line for line in plugin_lines)
        if not plugin_loaded:
            raise SmokeError(f"Bundled Intro Skipper did not load: {plugin_lines[-10:]}")
        receipt["fresh"] = {"server_id": info["Id"], "setup_was_incomplete": True,
                            "setup_completed": True, "user_id": user_id,
                            "item_id": str(item["Id"]), "item_path": item.get("Path"),
                            "range": stream, "intro_skipper_loaded": plugin_loaded,
                            "intro_skipper_log_lines": plugin_lines[-10:], "server_process": child}

        quit_app(process, work, port, operations)
        receipt["quit"] = {"app_exit": process.returncode, "server_stopped": True}
        child = None

        process = launch_app(executable, environment, app_log)
        receipt["relaunch_pid"] = process.pid
        child = server_child(process.pid, copied_app, data_path, expected_rid, operations)
        receipt["relaunch_server_process"] = child
        relaunched = public_info(base, operations, process)
        if relaunched.get("Id") != info.get("Id"):
            raise SmokeError(f"Server identity changed on relaunch: {info.get('Id')!r} -> {relaunched.get('Id')!r}")
        if relaunched.get("StartupWizardCompleted") is not True:
            raise SmokeError(f"Startup wizard is not complete after relaunch: {relaunched!r}")
        new_token, new_user_id = authenticate(base, operations)
        retained_query = urllib.parse.urlencode({"Recursive": "true", "IncludeItemTypes": "Movie", "Fields": "Path"})
        retained = json_api(base, "GET", "/Items?" + retained_query, operations, token=new_token).get("Items", [])
        if new_user_id != user_id or not any(str(row.get("Id")) == str(item["Id"]) for row in retained if isinstance(row, dict)):
            raise SmokeError(f"Relaunch changed the admin or library item: user={new_user_id}, items={retained[:10]!r}")
        quit_app(process, work, port, operations)
        receipt["relaunch"] = {"server_id": relaunched["Id"], "setup_complete": True,
                               "user_id": new_user_id, "item_id": str(item["Id"]),
                               "app_exit": process.returncode, "server_stopped": True,
                               "server_process": child}
        process = None

        run([sys.executable, "-c", "import shutil,sys; shutil.rmtree(sys.argv[1])", str(copied_app)],
            "Remove only the copied app", operations, timeout=60)
        receipt["app_removed"] = not copied_app.exists()
        receipt["profile_preserved"] = profile_path.is_dir()
        receipt["media_preserved"] = media.is_file()
        if not receipt["app_removed"] or not receipt["profile_preserved"] or not receipt["media_preserved"]:
            raise SmokeError(f"Removal checks failed: {receipt!r}")
        receipt["status"] = "passed"
        result_code = 0
    except BaseException as error:
        receipt["status"] = "failed"
        receipt["error"] = {"type": type(error).__name__, "message": str(error)}
        receipt["traceback"] = traceback.format_exc()
    finally:
        if process is not None and receipt["status"] != "passed":
            owned_processes_stopped = False
            try:
                for sig in (signal.SIGTERM, signal.SIGKILL):
                    if child is not None:
                        try:
                            if owned_server_alive(child, data_path, deadline):
                                os.kill(child["pid"], sig)
                                receipt.setdefault("cleanup", []).append({"server_pid": child["pid"], "signal": sig.name})
                        except ProcessLookupError:
                            receipt.setdefault("cleanup", []).append({"server_pid": child["pid"], "already_stopped": True})
                        except (OSError, SmokeError) as error:
                            receipt["cleanup_errors"].append(f"Could not inspect or signal server PID {child['pid']}: {error}")
                    if process.poll() is None:
                        try:
                            process.send_signal(sig)
                            receipt.setdefault("cleanup", []).append({"app_pid": process.pid, "signal": sig.name})
                        except ProcessLookupError:
                            receipt.setdefault("cleanup", []).append({"app_pid": process.pid, "already_stopped": True})
                        except OSError as error:
                            receipt["cleanup_errors"].append(f"Could not signal app PID {process.pid}: {error}")
                    try:
                        end = min(deadline.at, time.monotonic() + 5)
                        while time.monotonic() < end:
                            if process.poll() is not None and child is not None and not owned_server_alive(child, data_path, deadline):
                                break
                            time.sleep(min(0.2, max(0, end - time.monotonic())))
                        if process.poll() is not None and child is not None and not owned_server_alive(child, data_path, deadline):
                            owned_processes_stopped = True
                            break
                    except (OSError, SmokeError) as error:
                        receipt["cleanup_errors"].append(f"Could not verify server shutdown after {sig.name}: {error}")
                if not owned_processes_stopped:
                    receipt["cleanup_errors"].append("Could not confirm that the owned app and recorded server stopped; retained app.")
            except Exception as error:
                receipt["cleanup_errors"].append(f"Could not verify owned process cleanup: {error}")
            try:
                process.poll()  # Reap the launcher even when it has already become a zombie.
            except OSError as error:
                receipt["cleanup_errors"].append(f"Could not reap app PID {process.pid}: {error}")
        if copied_app is not None and copied_app.exists() and owned_processes_stopped and (process is None or process.poll() is not None):
            try:
                run([sys.executable, "-c", "import shutil,sys; shutil.rmtree(sys.argv[1])", str(copied_app)],
                    "Remove only the copied app after failure", deadline, timeout=30)
            except Exception as error:
                receipt["cleanup_errors"].append(f"Could not remove copied app {copied_app}: {error}")
                receipt["status"] = "failed"
                result_code = 1
        if device:
            try:
                run(["/usr/bin/hdiutil", "detach", str(device)], "Detach mounted DMG", deadline, timeout=30)
            except Exception as error:
                receipt["cleanup_errors"].append(str(error))
                receipt["status"] = "failed"
                result_code = 1
        try:
            receipt["logs"] = {"native_app": str(app_log), "native_app_tail": log_tail(app_log),
                               "server": str(server_log) if server_log else None,
                               "server_tail": log_tail(server_log) if server_log else ""}
        except OSError as error:
            receipt["cleanup_errors"].append(f"Could not collect log tails: {error}")
        receipt["finished_at"] = dt.datetime.now(dt.timezone.utc).isoformat()
        receipt["duration_seconds"] = round(time.monotonic() - started, 2)
        receipt["mounted_volume"] = str(mounted) if mounted else None
        receipt["retained_profile"] = str(profile["profile"]) if profile else None
        receipt["retained_media"] = str(media) if media else None
        try:
            write_json(receipt_path, receipt)
        except OSError as error:
            print(f"Could not write receipt {receipt_path}: {error}", file=sys.stderr)
            result_code = 1

    print(f"{receipt['status'].upper()}: receipt {receipt_path}")
    if receipt.get("error"):
        print(receipt["error"]["message"], file=sys.stderr)
    if receipt.get("cleanup_errors"):
        print("Cleanup errors: " + "; ".join(receipt["cleanup_errors"]), file=sys.stderr)
    return result_code


if __name__ == "__main__":
    raise SystemExit(main())
