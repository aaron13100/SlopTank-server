#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05, 2026-11-11, 2026-11-12, 2026-11-13, 2026-11-14.
"""Run backend API checks and optional GUI acceptance against a SlopTank DMG.

Usage: python3 apps/macos/smoke-installed-app.py --dmg SlopTank.dmg \
    --work-dir "$(mktemp -d "${TMPDIR:-/private/tmp}/sloptank-smoke.XXXXXX")"

The work directory retains logs, generated media, profiles and JSON receipts.
The optional GUI journey is fail-closed to its authorized GitHub macOS guest.
Only owned app copies are removed. The mounted image is always detached.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import platform
import plistlib
import shutil
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
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


# The same portable helper lives under tools in the operations checkout.
preparation_source = Path(__file__).resolve().parents[1] / "preparation"
if not preparation_source.is_dir():
    preparation_source = Path(__file__).resolve().parents[2] / "tools"
sys.path.insert(0, str(preparation_source))
from lib.load_scaled_timeout import load_scaled_timeout
from owned_app_processes import launch_app, quit_app, server_child, stop_owned_processes
from smoke_deadline import POLL_SECONDS, Deadline, SmokeError, run
from smoke_server_api import api, json_api
from playback_retention import capture_playback_state, verify_playback_state
from macos_distribution import distribution_checks
from smoke_gui_controls import write_json, run_welcome_ui, _run_browser_wizard
from smoke_receipt_reuse import reusable_journeys


SYSTEM_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"
APP_NAME = "SlopTank.app"
PROFILE_RELATIVE = Path("Library/Application Support/SlopTank")
ADMIN_NAME = "smoke-admin"
ADMIN_PASSWORD = "Smoke-only-password-4917!"
MEDIA_NAME = "SlopTank Installed App Smoke.mp4"
UNREADY_NAME = "SlopTank Preparation Smoke.mkv"
CLEANUP_RESERVE_SECONDS = 105


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


def _read_profile(home: Path) -> tuple[dict[str, Any], Path]:
    profile_file = home / PROFILE_RELATIVE / "profile.json"
    if not profile_file.is_file() or profile_file.is_symlink():
        raise SmokeError(f"Launcher did not write a regular profile record under its isolated home: {profile_file}")
    try:
        profile = json.loads(profile_file.read_text(encoding="utf-8"))
        selected_path = Path(profile["selectedRootPath"])
        if selected_path.is_symlink():
            raise SmokeError(f"Selected data root must not be a symlink: {selected_path}")
        selected_root = selected_path.resolve(strict=True)
    except (OSError, KeyError, TypeError, ValueError) as error:
        raise SmokeError(f"Could not read the initialized profile at {profile_file}: {error}") from error
    if not profile_file.resolve().is_relative_to(home.resolve()) or not selected_root.is_dir():
        raise SmokeError(f"Selected data root is not an owned real directory: {selected_root}")
    return profile, selected_root


def _wait_profile_ready(home: Path, process: subprocess.Popen[bytes],
                        deadline: Deadline) -> tuple[dict[str, Any], Path]:
    """Wait for the launcher to record a ready profile; its first startup completes asynchronously.

    The Start button returns before the server is ready, and the launcher writes
    startupCompleted only after readiness, so a single read races it.
    """
    last = "profile.json not written yet"
    while deadline.remaining() > 0:
        if process.poll() is not None:
            raise SmokeError(f"Native app exited {process.returncode} before completing its profile initialization: {last}")
        try:
            profile, profile_root = _read_profile(home)
        except SmokeError as error:
            last = str(error)
        else:
            if profile.get("startupCompleted") is True and profile.get("initializationState") == "ready":
                return profile, profile_root
            last = f"profile not ready: {profile!r}"
        time.sleep(min(POLL_SECONDS, max(0, deadline.remaining())))
    raise SmokeError(f"Launcher did not complete its profile initialization before the deadline: {last}")


def _profile_port(profile_root: Path) -> int:
    network_file = profile_root / "Configuration/network.xml"
    try:
        root = ET.parse(network_file).getroot()
        port = int(root.findtext("InternalHttpPort", ""))
    except (OSError, ValueError, ET.ParseError) as error:
        raise SmokeError(f"Could not read the launcher-created network configuration {network_file}: {error}") from error
    if not 1024 <= port <= 65535:
        raise SmokeError(f"Launcher-created server port is invalid: {port}")
    return port


def _make_isolated_environment(home: Path) -> dict[str, str]:
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith(("JELLYFIN_", "SLOPTANK_", "DOTNET_"))}
    temporary = home / "tmp"
    temporary.mkdir(mode=0o700, parents=True, exist_ok=True)
    environment.update({"PATH": SYSTEM_PATH, "HOME": str(home), "CFFIXED_USER_HOME": str(home),
                        "TMPDIR": str(temporary)})
    return environment


def _user_defaults_key_present(bundle_id: str, environment: dict[str, str], deadline: Deadline) -> bool:
    result = run(["/usr/bin/defaults", "read", bundle_id, "serverDataRootBookmark"],
                 f"Inspect guest UserDefaults bookmark for {bundle_id}", deadline,
                 timeout=10, check=False, env=environment)
    return result.returncode == 0


def _capture_bookmark_preferences(bundle_id: str, environment: dict[str, str], output: Path,
                                  deadline: Deadline) -> Path:
    """Capture the effective guest-user domain after quit, without assuming its disk path."""
    result = run(["/usr/bin/defaults", "export", bundle_id, "-"],
                 f"Export guest UserDefaults domain for {bundle_id}", deadline,
                 timeout=10, env=environment)
    try:
        values = plistlib.loads(result.stdout.encode("utf-8"))
    except plistlib.InvalidFileException as error:
        raise SmokeError(f"UserDefaults export for {bundle_id} was not a valid plist: {error}") from error
    bookmark = values.get("serverDataRootBookmark") if isinstance(values, dict) else None
    if not isinstance(bookmark, bytes) or not bookmark:
        raise SmokeError(f"UserDefaults domain {bundle_id} has no nonempty data bookmark after app quit.")
    output.write_bytes(plistlib.dumps(values))
    return output


def _bundle_version(app: Path) -> dict[str, str]:
    """Read the real version fields without changing the supplied app bundle."""
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    result = {"bundle_id": info.get("CFBundleIdentifier"),
              "version": info.get("CFBundleShortVersionString"), "build": info.get("CFBundleVersion")}
    if (not all(isinstance(value, str) and value for value in result.values())
            or not all(all(part.isdecimal() for part in result[key].split(".")) for key in ("version", "build"))):
        raise SmokeError(f"App has no comparable bundle version and build: {app}: {result!r}")
    return result


def _regular_dmg(value: str) -> Path:
    """Normalize an image argument before any workspace or mount is created."""
    image = Path(value).expanduser().absolute()
    if not image.is_file() or image.is_symlink():
        raise argparse.ArgumentTypeError(f"Expected a regular DMG file: {image}")
    return image


def _mount_app_image(dmg: Path, mounts: Path, devices: list[str], deadline: Deadline) -> Path:
    """Mount inside the owned workspace and register its device before validation."""
    mounts.mkdir(mode=0o700, parents=True, exist_ok=True)
    attach = run(["/usr/bin/hdiutil", "attach", "-readonly", "-nobrowse", "-plist",
                  "-mountroot", str(mounts), str(dmg)], "Mount DMG read-only", deadline, timeout=90)
    attached = plistlib.loads(attach.stdout.encode())
    entities = attached.get("system-entities", [])
    device = next((entry.get("dev-entry") for entry in entities if entry.get("dev-entry")), None)
    if device:
        devices.append(str(device))
    mount_entry = next((entry for entry in entities if entry.get("mount-point")), None)
    if not mount_entry or not device:
        raise SmokeError(f"hdiutil attach omitted a mounted volume or device: {attached!r}")
    mounted = Path(mount_entry["mount-point"]).resolve()
    if not mounted.is_relative_to(mounts.resolve()):
        raise SmokeError(f"Image mounted outside its owned directory: {mounted}")
    return mounted


def _replace_stopped_app(source_app: Path, copied_app: Path, deadline: Deadline) -> None:
    """Replace an owned app after its launcher and server have been stopped."""
    replacement = copied_app.with_name("SlopTank.replacement.app")
    previous = copied_app.with_name("SlopTank.previous.app")
    if replacement.exists() or replacement.is_symlink() or previous.exists() or previous.is_symlink():
        raise SmokeError("Owned app replacement paths are unexpectedly occupied.")
    run(["/usr/bin/ditto", str(source_app), str(replacement)],
        "Copy the app bundle for stopped-app replacement", deadline, timeout=480)
    os.replace(copied_app, previous)
    try:
        os.replace(replacement, copied_app)
    except OSError:
        os.replace(previous, copied_app)
        raise
    run([sys.executable, "-c", "import shutil,sys; shutil.rmtree(sys.argv[1])", str(previous)],
        "Remove the owned previous app copy after replacement", deadline, timeout=120)


def run_gui_acceptance(source_app: Path, copied_app: Path, work: Path, media_directory: Path,
                       media: Path, expected_rid: str, live: dict[str, Any],
                       journeys: dict[str, Any], deadline: Deadline, *,
                       baseline_app: Path | None = None) -> dict[str, Any]:
    """Exercise fresh native paths and an optional genuine older-build upgrade."""
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_OS") != "macOS" or not os.environ.get("GITHUB_RUN_ID"):
        raise SmokeError("Interactive acceptance is allowed only inside its authorized GitHub macOS guest.")
    if not shutil.which("node"):
        raise SmokeError("Node.js is unavailable for the pinned browser acceptance driver.")
    if not os.environ.get("SLOPTANK_PLAYWRIGHT_NODE_MODULES"):
        raise SmokeError("SLOPTANK_PLAYWRIGHT_NODE_MODULES is required for GUI acceptance.")

    node = shutil.which("node")
    node_bin_dir = Path(node).resolve().parent
    executable = copied_app / "Contents/MacOS/SlopTank"
    bundle_info = plistlib.loads((copied_app / "Contents/Info.plist").read_bytes())
    bundle_id = bundle_info.get("CFBundleIdentifier")
    if not isinstance(bundle_id, str) or not bundle_id:
        raise SmokeError("Installed app Info.plist omitted CFBundleIdentifier.")
    journeys.update({"scope": "GitHub macOS guest only", "status": "running",
                     "bundle_id": bundle_id, "runtime_id": expected_rid})
    media_directory = media_directory.resolve(strict=True)
    media = media.resolve(strict=True)
    if not media.is_file() or not media.is_relative_to(media_directory):
        raise SmokeError(f"The GUI media fixture must be inside the owned media directory: {media}")
    app_log: Path | None = None

    def launch_journey(name: str, home: Path, action: str, selected_root: Path | None = None) -> tuple[subprocess.Popen[bytes], dict[str, Any], Path, int, dict[str, Any], dict[str, str], dict[str, Any]]:
        nonlocal app_log
        home.mkdir(mode=0o700, parents=True, exist_ok=True)
        support = home / PROFILE_RELATIVE
        if support.exists() or support.is_symlink():
            raise SmokeError(f"GUI journey must start with an empty isolated home: {support}")
        if selected_root is not None:
            selected_root.mkdir(mode=0o700, parents=True)
            if any(selected_root.iterdir()):
                raise SmokeError(f"Alternate data folder must be empty before selection: {selected_root}")
        environment = _make_isolated_environment(home)
        if _user_defaults_key_present(bundle_id, environment, deadline):
            raise SmokeError(f"Isolated GUI home already contains a data-root bookmark: {home}")
        app_log = work / name / "native-app.log"
        app_log.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        expected_root = selected_root or home / PROFILE_RELATIVE / "ServerProfile"
        journey_key = {"gui-default": "default_root", "gui-alternate": "alternate_root", "gui-upgrade": "upgrade"}[name]
        journeys[journey_key].update({"native_app_log": str(app_log),
                                      "server_log": str(expected_root / "Logs/server.log"),
                                      "welcome_control": str(Path(__file__).with_name("welcome-ui.js"))})
        process = launch_app(executable, environment, app_log)
        live.update({"process": process, "child": None, "data_path": expected_root / "Data",
                     "server_log": expected_root / "Logs/server.log", "home": home, "app_log": app_log})
        run_welcome_ui(process.pid, copied_app, home, action, work / name, deadline, selected_root)
        profile, profile_root = _wait_profile_ready(home, process, deadline)
        if selected_root is not None and profile_root != selected_root.resolve(strict=True):
            raise SmokeError(f"Welcome chooser selected {selected_root}, profile records {profile_root}.")
        if selected_root is None and profile_root != (home / PROFILE_RELATIVE / "ServerProfile").resolve():
            raise SmokeError(f"Default welcome flow did not keep its normal data root: {profile_root}")
        data_path = profile_root / "Data"
        server_log = profile_root / "Logs/server.log"
        port = _profile_port(profile_root)
        child = server_child(process.pid, copied_app, data_path, expected_rid, deadline)
        live.update({"process": process, "child": child, "data_path": data_path,
                     "server_log": server_log, "home": home, "app_log": app_log})
        info = public_info(f"http://127.0.0.1:{port}", deadline, process)
        if info.get("StartupWizardCompleted") is not False:
            raise SmokeError(f"Fresh UI journey should begin at the real browser wizard: {info!r}")
        return process, profile, profile_root, port, child, environment, info

    def quit_journey(process: subprocess.Popen[bytes], port: int) -> None:
        quit_app(process, work, port, deadline)
        live.update({"process": None, "child": None, "data_path": None, "server_log": None,
                     "home": None, "app_log": None})

    def default_journey(name: str) -> dict[str, Path]:
        journey_key = {"gui-default": "default_root", "gui-upgrade": "upgrade"}[name]
        before_version = _bundle_version(copied_app)
        target_version = _bundle_version(source_app)
        default_home = work / name / "isolated-home"
        default_work = work / name
        journeys[journey_key] = {"status": "running", "welcome_action": "Start SlopTank"}
        default_process, default_profile, default_root, default_port, default_child, default_env, default_info = launch_journey(
            name, default_home, "start-default")
        default_server_id = str(default_info["Id"])
        journeys[journey_key].update({"server_log": str(default_root / "Logs/server.log")})
        if _user_defaults_key_present(bundle_id, default_env, deadline):
            raise SmokeError("The normal default-root journey unexpectedly stored a location bookmark.")
        default_base = f"http://127.0.0.1:{default_port}"
        browser_env = dict(default_env)
        browser_env["NODE_PATH"] = os.environ["SLOPTANK_PLAYWRIGHT_NODE_MODULES"]
        browser_env["PATH"] = SYSTEM_PATH + os.pathsep + str(node_bin_dir)
        browser_receipt = _run_browser_wizard(default_base, media_directory, media, "Smoke Movies",
                                              ADMIN_NAME, ADMIN_PASSWORD, node, default_work, browser_env, deadline)
        token, user_id = authenticate(default_base, deadline)
        item_query = urllib.parse.urlencode({"Recursive": "true", "IncludeItemTypes": "Movie", "Fields": "Path"})
        items = json_api(default_base, "GET", "/Items?" + item_query, deadline, token=token).get("Items", [])
        media_items = [row for row in items if isinstance(row, dict)
                       and Path(str(row.get("Path", ""))).resolve() == media]
        if len(media_items) != 1:
            raise SmokeError(f"Browser wizard did not create exactly one library item for {media}: {media_items!r}")
        item_id = str(media_items[0].get("Id"))
        playback_report = browser_receipt.get("serverPlaybackReport")
        if (browser_receipt.get("itemId") != item_id or not isinstance(playback_report, dict)
                or playback_report.get("nowPlayingItemId") != item_id):
            raise SmokeError(f"Browser journey did not play the expected library item {item_id}: {browser_receipt!r}")
        user_data = json_api(default_base, "GET", f"/Users/{user_id}/Items/{item_id}", deadline, token=token).get("UserData")
        if not isinstance(user_data, dict) or int(user_data.get("PlayCount") or 0) < 1:
            raise SmokeError(f"Server recorded no playback start for the UI-played item {item_id}: {user_data!r}")
        played_state = capture_playback_state({"base": default_base, "token": token,
                                               "user_id": user_id, "item_id": item_id}, deadline)
        default_profile_id = default_profile.get("profileID")
        if not default_profile_id:
            raise SmokeError("Default welcome journey did not record its private profile ID.")
        journeys[journey_key].update({"profile": str(default_root), "profile_id": default_profile_id,
                                         "server_id": default_info["Id"], "browser": browser_receipt,
                                         "admin_user_id": user_id, "library_item_id": item_id,
                                         "server_playback": {"play_count": user_data.get("PlayCount"),
                                                             "last_played_date": user_data.get("LastPlayedDate"),
                                                             "session_report": playback_report},
                                         "backend_api_coverage": "API confirms UI-created account/library item and its playback start",
                                         "user_defaults_bookmark_present": False})
        quit_journey(default_process, default_port)

        _replace_stopped_app(source_app, copied_app, deadline)

        replacement_env = _make_isolated_environment(default_home)
        default_process = launch_app(executable, replacement_env, work / name / "replacement-app.log")
        live.update({"process": default_process, "child": None, "data_path": None,
                     "server_log": None, "home": default_home, "app_log": work / name / "replacement-app.log"})
        profile_after, root_after = _read_profile(default_home)
        if profile_after.get("profileID") != default_profile_id or root_after != default_root:
            raise SmokeError("App replacement changed the default profile identity or data root.")
        default_data = root_after / "Data"
        default_log = root_after / "Logs/server.log"
        default_port = _profile_port(root_after)
        default_child = server_child(default_process.pid, copied_app, default_data, expected_rid, deadline)
        live.update({"process": default_process, "child": default_child, "data_path": default_data,
                     "server_log": default_log, "home": default_home, "app_log": work / name / "replacement-app.log"})
        journeys[journey_key]["replacement_app_log"] = str(work / name / "replacement-app.log")
        default_info = public_info(f"http://127.0.0.1:{default_port}", deadline, default_process)
        run_welcome_ui(default_process.pid, copied_app, default_home, "assert-no-welcome", work / name, deadline)
        if default_info.get("Id") != default_server_id:
            raise SmokeError(f"App replacement changed the server identity: {default_server_id!r} -> {default_info.get('Id')!r}")
        if default_info.get("StartupWizardCompleted") is not True:
            raise SmokeError(f"App replacement did not preserve completed web setup: {default_info!r}")
        replacement_token, replacement_user = authenticate(f"http://127.0.0.1:{default_port}", deadline)
        retained = json_api(f"http://127.0.0.1:{default_port}", "GET", "/Items?" + item_query,
                            deadline, token=replacement_token).get("Items", [])
        if replacement_user != user_id or not any(isinstance(row, dict) and str(row.get("Id")) == item_id for row in retained):
            raise SmokeError("App replacement did not preserve the admin and UI-created library item.")
        journeys[journey_key]["playback_retention"] = verify_playback_state(
            {"base": f"http://127.0.0.1:{default_port}", "token": replacement_token,
             "user_id": replacement_user, "item_id": item_id}, played_state, deadline)
        if not media.is_file():
            raise SmokeError("App replacement removed the retained scratch media.")
        journeys[journey_key]["version_transition" if before_version != target_version else "same_version_replacement"] = {
            "from": before_version, "to": target_version,
            "profile_id": profile_after.get("profileID"), "selected_root": str(root_after),
            "server_id": default_info.get("Id"), "admin_user_id": replacement_user,
            "library_item_retained": True, "media_retained": True, "welcome_reappeared": False
        }
        quit_journey(default_process, default_port)

        journeys[journey_key]["status"] = "passed"
        return {"home": default_home, "root": default_root}

    retained_profiles = {"media": media.is_file()}
    for key, name, source in (("default_root", "gui-default", None), ("upgrade", "gui-upgrade", baseline_app)):
        if key == "upgrade" and source is None:
            continue
        if journeys.get(key, {}).get("status") != "passed":
            if source is not None:
                _replace_stopped_app(source, copied_app, deadline)
            observed = default_journey(name)
            retained_profiles["upgrade" if key == "upgrade" else "default"] = (
                (observed["home"] / PROFILE_RELATIVE / "profile.json").is_file() and observed["root"].is_dir())
        else:
            retained_profiles["upgrade" if key == "upgrade" else "default"] = True

    if journeys.get("alternate_root", {}).get("status") == "passed":
        retained_profiles["alternate"] = True
    else:
        alternate_home = work / "gui-alternate/isolated-home"
        alternate_root = work / "gui-alternate/empty-server-data"
        journeys["alternate_root"] = {"status": "running", "welcome_action": "choose empty alternate folder, then Start SlopTank"}
        alternate_process, alternate_profile, actual_alternate_root, alternate_port, alternate_child, alternate_env, alternate_info = launch_journey(
            "gui-alternate", alternate_home, "choose-and-start", alternate_root)
        journeys["alternate_root"].update({"native_app_log": str(app_log),
                                           "server_log": str(actual_alternate_root / "Logs/server.log"),
                                           "welcome_control": str(Path(__file__).with_name("welcome-ui.js"))})
        alternate_base = f"http://127.0.0.1:{alternate_port}"
        alternate_profile_id = alternate_profile.get("profileID")
        if not alternate_profile_id or not (actual_alternate_root / ".sloptank-profile").is_file():
            raise SmokeError("Alternate-root welcome journey did not persist its profile marker and identity.")
        if not _user_defaults_key_present(bundle_id, alternate_env, deadline):
            raise SmokeError("Alternate data-root bookmark is not readable in the guest UserDefaults domain.")
        journeys["alternate_root"].update({"selected_root": str(actual_alternate_root),
                                          "profile_id": alternate_profile_id, "server_id": alternate_info["Id"],
                                          "user_defaults_bookmark_present": True,
                                          "browser_setup": "intentionally not repeated on this second fresh profile"})
        quit_journey(alternate_process, alternate_port)
        # CFPreferences belongs to the ephemeral runner user; HOME isolates app files, not that service.
        preferences_file = _capture_bookmark_preferences(
            bundle_id, alternate_env, work / "gui-alternate/bookmark-preferences.plist", deadline)
        journeys["alternate_root"]["preferences_capture"] = str(preferences_file)
        journeys["alternate_root"]["preferences_scope"] = "ephemeral macOS guest user"

        alternate_env = _make_isolated_environment(alternate_home)
        alternate_process = launch_app(executable, alternate_env, work / "gui-alternate/relaunch-app.log")
        live.update({"process": alternate_process, "child": None, "data_path": None,
                     "server_log": None, "home": alternate_home, "app_log": work / "gui-alternate/relaunch-app.log"})
        alternate_after, alternate_root_after = _read_profile(alternate_home)
        if alternate_after.get("profileID") != alternate_profile_id or alternate_root_after != actual_alternate_root:
            raise SmokeError("Alternate-root relaunch changed the selected folder or profile identity.")
        alternate_data = alternate_root_after / "Data"
        alternate_log = alternate_root_after / "Logs/server.log"
        alternate_port = _profile_port(alternate_root_after)
        alternate_child = server_child(alternate_process.pid, copied_app, alternate_data, expected_rid, deadline)
        live.update({"process": alternate_process, "child": alternate_child, "data_path": alternate_data,
                     "server_log": alternate_log, "home": alternate_home,
                     "app_log": work / "gui-alternate/relaunch-app.log"})
        journeys["alternate_root"]["relaunch_app_log"] = str(work / "gui-alternate/relaunch-app.log")
        alternate_after_info = public_info(f"http://127.0.0.1:{alternate_port}", deadline, alternate_process)
        run_welcome_ui(alternate_process.pid, copied_app, alternate_home, "assert-no-welcome",
                       work / "gui-alternate", deadline)
        if alternate_after_info.get("Id") != alternate_info.get("Id") or alternate_after_info.get("StartupWizardCompleted") is not False:
            raise SmokeError("Alternate-root relaunch did not preserve its running server identity and pending browser wizard.")
        if not _user_defaults_key_present(bundle_id, alternate_env, deadline):
            raise SmokeError("Alternate data-root bookmark disappeared after launcher restart.")
        journeys["alternate_root"]["relaunch"] = {
            "selected_root": str(alternate_root_after), "profile_id": alternate_after.get("profileID"),
            "server_id": alternate_after_info.get("Id"), "server_data_path": str(alternate_data),
            "welcome_reappeared": False, "wizard_remains_pending": True
        }
        quit_journey(alternate_process, alternate_port)
        retained_profiles["alternate"] = (alternate_home / PROFILE_RELATIVE / "profile.json").is_file() and alternate_root_after.is_dir()
    if not all(retained_profiles.values()):
        raise SmokeError(f"A GUI journey did not retain its isolated profile or scratch media: {retained_profiles!r}")
    journeys["retained_after_both_journeys"] = retained_profiles
    journeys["alternate_root"]["status"] = "passed"
    journeys["default_root"]["status"] = "passed"
    journeys["status"] = "passed"
    return journeys


def log_tail(path: Path, limit: int = 20000) -> str:
    if not path.is_file():
        return ""
    with path.open("rb") as stream:
        stream.seek(max(0, path.stat().st_size - limit))
        return stream.read().decode("utf-8", errors="replace")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dmg", required=True, type=_regular_dmg)
    parser.add_argument("--baseline-dmg", type=_regular_dmg, help="Also exercise a genuine older signed app before replacing it with this candidate")
    parser.add_argument("--reuse-smoke-receipt", type=Path, help="Reuse only validated passed GUI rows for the same signed app/runtime")
    parser.add_argument("--require-notarized", action="store_true", help="Require Developer ID, hardened runtime, a stapled DMG and Gatekeeper acceptance")
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=int, default=1200, help="One deadline for the full journey and cleanup; default 1200")
    parser.add_argument("--gui-acceptance", action="store_true",
                        help="Also run PID-scoped native welcome and real browser journeys; GitHub macOS guest only")
    args = parser.parse_args()
    if args.baseline_dmg and not args.gui_acceptance:
        parser.error("--baseline-dmg requires the authorized guest-only --gui-acceptance journey.")
    if args.reuse_smoke_receipt and not (args.gui_acceptance and args.require_notarized):
        parser.error("--reuse-smoke-receipt requires guest GUI acceptance and current notarized distribution checks.")
    if sys.platform != "darwin":
        parser.error("This installed-app smoke runs only on macOS.")
    if args.gui_acceptance and not (
            os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_OS") == "macOS"
            and os.environ.get("GITHUB_RUN_ID")):
        parser.error("--gui-acceptance is permitted only inside the authorized GitHub macOS guest.")
    if args.gui_acceptance and not os.environ.get("SLOPTANK_PLAYWRIGHT_NODE_MODULES"):
        parser.error("--gui-acceptance requires the pinned SLOPTANK_PLAYWRIGHT_NODE_MODULES installation.")
    if args.gui_acceptance and not Path("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome").is_file():
        parser.error("--gui-acceptance requires the runner's installed Google Chrome browser.")
    if args.timeout_seconds < 300:
        parser.error("--timeout-seconds must be at least 300")
    dmg = args.dmg
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

    with dmg.open("rb") as image:
        dmg_sha256 = hashlib.file_digest(image, "sha256").hexdigest()
    started = time.monotonic()
    deadline = Deadline(args.timeout_seconds)
    operations = Deadline(max(1, args.timeout_seconds - CLEANUP_RESERVE_SECONDS))
    operations.at = deadline.at - CLEANUP_RESERVE_SECONDS
    receipt_path = work / "smoke-installed-app.json"
    receipt: dict[str, Any] = {"schemaVersion": 1, "type": "sloptank-installed-app-smoke",
                               "status": "running", "started_at": dt.datetime.now(dt.timezone.utc).isoformat(),
                               "dmg": str(dmg), "dmg_sha256": dmg_sha256, "github_run_id": os.environ.get("GITHUB_RUN_ID"),
                               "work_dir": str(work), "timeout_seconds": args.timeout_seconds,
                               "machine": platform.machine(), "cleanup_errors": [],
                               "coverage": ["backend-api"] + (["native-welcome-and-browser-ui"] if args.gui_acceptance else [])}
    mounts = work / "mounts"
    apps = work / "Applications"
    home = work / "isolated-home"
    media_dir = work / "scratch-media/Movies"
    for folder in (mounts, apps, home, media_dir):
        folder.mkdir(mode=0o700, parents=True)
    mounted: Path | None = None
    devices: list[str] = []
    baseline_app: Path | None = None
    copied_app: Path | None = None
    process: subprocess.Popen[bytes] | None = None
    child: dict[str, Any] | None = None
    gui_live: dict[str, Any] = {}
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
        mounted = _mount_app_image(dmg, mounts, devices, operations)
        source_app = mounted / APP_NAME
        if not source_app.is_dir() or source_app.is_symlink():
            raise SmokeError(f"Mounted image has no regular {APP_NAME}: {source_app}")
        if args.require_notarized:
            receipt["distribution"] = distribution_checks(dmg, source_app, operations, notarized=True)
            receipt["coverage"].append("developer-id-notarized")
        if args.baseline_dmg:
            baseline_mount = _mount_app_image(args.baseline_dmg, mounts / "baseline", devices, operations)
            baseline_app = baseline_mount / APP_NAME
            if not baseline_app.is_dir() or baseline_app.is_symlink():
                raise SmokeError(f"Baseline image has no regular {APP_NAME}: {baseline_app}")
            before, after = _bundle_version(baseline_app), _bundle_version(source_app)
            old_key = [tuple(map(int, before[field].split("."))) for field in ("version", "build")]
            new_key = [tuple(map(int, after[field].split("."))) for field in ("version", "build")]
            if before["bundle_id"] != after["bundle_id"] or old_key >= new_key:
                raise SmokeError(f"Upgrade requires the same app and a genuinely earlier version/build: {before!r} -> {after!r}")
            receipt["upgrade_baseline"] = {"dmg": str(args.baseline_dmg), "from": before, "to": after,
                                           "signature": distribution_checks(args.baseline_dmg, baseline_app, operations, notarized=False)}
            receipt["coverage"].append("signed-older-build-upgrade")
        copied_app = apps / APP_NAME
        run(["/usr/bin/ditto", str(source_app), str(copied_app)], "Copy app to isolated Applications", operations, timeout=480)
        executable = copied_app / "Contents/MacOS/SlopTank"
        ffmpeg = copied_app / "Contents/Resources/ffmpeg/ffmpeg"
        ffprobe = copied_app / "Contents/Resources/ffmpeg/ffprobe"
        if not executable.is_file() or not os.access(executable, os.X_OK):
            raise SmokeError(f"Installed bundle has no executable launcher: {executable}")
        if not ffmpeg.is_file() or not os.access(ffmpeg, os.X_OK):
            raise SmokeError(f"Installed bundle has no executable bundled ffmpeg: {ffmpeg}")
        if not ffprobe.is_file() or not os.access(ffprobe, os.X_OK):
            raise SmokeError(f"Installed bundle has no executable bundled ffprobe: {ffprobe}")

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
             "-t", "12", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "24", "-keyint_min", "24",
             "-c:a", "aac", "-movflags", "+faststart", "-shortest", str(media)],
            "Generate H.264/AAC MP4 with bundled ffmpeg", operations, timeout=90)
        receipt["media"] = {"path": str(media), "size_bytes": media.stat().st_size,
                            "sha256": hashlib.sha256(media.read_bytes()).hexdigest()}

        if args.gui_acceptance:
            receipt["gui_acceptance"] = {"status": "running", "journeys": ["default-root", "alternate-empty-root"] + (["older-build-upgrade"] if baseline_app else [])}
            if args.reuse_smoke_receipt:
                prior_path = args.reuse_smoke_receipt
                if prior_path.is_symlink() or not prior_path.is_file():
                    raise SmokeError(f"Prior smoke receipt must be a regular file: {prior_path}")
                raw = prior_path.read_bytes()
                prior = json.loads(raw)
                reused = reusable_journeys(prior, distribution=receipt["distribution"], runtime_id=expected_rid,
                                           bundle_version=_bundle_version(source_app), baseline=receipt.get("upgrade_baseline"),
                                           dmg_sha256=dmg_sha256)
                (work / "prior-smoke-original.json").write_bytes(raw)
                receipt["gui_reuse"] = {"source_run_id": os.environ.get("SLOPTANK_REUSE_SMOKE_RUN_ID"),
                    "source_receipt": str(prior_path), "source_receipt_sha256": hashlib.sha256(raw).hexdigest(),
                    "source_status": prior.get("status"), "source_started_at": prior.get("started_at"),
                    "source_finished_at": prior.get("finished_at"), "journeys": list(reused)}
                receipt["gui_acceptance"].update(reused)
                receipt["gui_acceptance"]["reused_journeys"] = list(reused)
            receipt["gui_acceptance"] = run_gui_acceptance(source_app, copied_app, work, media_dir, media,
                                                           expected_rid, gui_live, receipt["gui_acceptance"], operations,
                                                           baseline_app=baseline_app)

        unready = media_dir / UNREADY_NAME
        run([str(ffmpeg), "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=c=black:s=320x180:r=24",
             "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
             "-t", "3", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", str(unready)],
            "Generate unready MPEG-4/AAC MKV with bundled ffmpeg", operations, timeout=90)
        original_hash = hashlib.sha256(unready.read_bytes()).hexdigest()

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
        prepared = unready.with_suffix(".mp4")
        prepared_item = wait_movie(base, token, prepared, operations)
        probe_result = run([str(ffprobe), "-v", "error", "-show_entries",
                            "stream=codec_type,codec_name,sample_rate", "-of", "json", str(prepared)],
                           "Probe prepared media with bundled ffprobe", operations, timeout=90)
        try:
            streams = json.loads(probe_result.stdout)["streams"]
        except (ValueError, KeyError, TypeError) as error:
            raise SmokeError(f"Prepared media probe returned invalid streams: {probe_result.stdout!r}") from error
        if (not any(s.get("codec_type") == "video" and s.get("codec_name") == "h264" for s in streams)
                or not any(s.get("codec_type") == "audio" and s.get("codec_name") == "aac"
                           and str(s.get("sample_rate")) == "48000" for s in streams)):
            raise SmokeError(f"Prepared media is not H.264 with AAC 48 kHz: {streams!r}")
        prepared_stream = direct_range(base, token, str(prepared_item["Id"]), prepared, operations)
        retired_dir = data_path / "retired-media"
        retired = [path for path in retired_dir.glob("*-" + UNREADY_NAME) if path.is_file()
                   and hashlib.sha256(path.read_bytes()).hexdigest() == original_hash]
        if unready.exists() or not retired or retired_dir.resolve().is_relative_to(media_dir.resolve()):
            raise SmokeError(f"Original was not retained outside the library: source={unready.exists()}, retired={retired!r}")
        receipt["preparation"] = {"item_id": str(prepared_item["Id"]), "path": str(prepared),
                                  "streams": streams, "range": prepared_stream,
                                  "original_sha256": original_hash, "retired_original": str(retired[0])}
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

        receipt["backend_api_coverage"] = {
            "status": "passed",
            "description": "Noninteractive server/API and native-helper coverage; does not substitute for GUI acceptance.",
            "checks": ["startup setup API", "library registration and refresh API", "direct stream Range response",
                       "preparation worker conversion and retained original", "normal quit and relaunch persistence"]
        }

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
        active_process = process if process is not None else gui_live.get("process")
        active_child = child if child is not None else gui_live.get("child")
        active_data_path = data_path if process is not None else gui_live.get("data_path")
        if receipt["status"] != "passed" and (active_process is not None or copied_app is not None):
            owned_processes_stopped = stop_owned_processes(active_process, active_child, active_data_path,
                                                           copied_app, receipt, deadline)
        if copied_app is not None and copied_app.exists() and owned_processes_stopped and (active_process is None or active_process.poll() is not None):
            try:
                run([sys.executable, "-c", "import shutil,sys; shutil.rmtree(sys.argv[1])", str(copied_app)],
                    "Remove only the copied app after failure", deadline, timeout=30)
            except Exception as error:
                receipt["cleanup_errors"].append(f"Could not remove copied app {copied_app}: {error}")
                receipt["status"] = "failed"
                result_code = 1
        if copied_app is not None and owned_processes_stopped:
            for temporary_app in (copied_app.with_name("SlopTank.replacement.app"),
                                  copied_app.with_name("SlopTank.previous.app")):
                if temporary_app.is_symlink():
                    receipt["cleanup_errors"].append(f"Refused to remove symlink at owned replacement path {temporary_app}")
                    receipt["status"] = "failed"
                    result_code = 1
                    continue
                if temporary_app.exists():
                    try:
                        run([sys.executable, "-c", "import shutil,sys; shutil.rmtree(sys.argv[1])", str(temporary_app)],
                            "Remove only an owned incomplete replacement copy", deadline, timeout=120)
                    except Exception as error:
                        receipt["cleanup_errors"].append(f"Could not remove owned replacement copy {temporary_app}: {error}")
                        receipt["status"] = "failed"
                        result_code = 1
        for device in reversed(devices):
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
            if args.gui_acceptance:
                gui_logs: dict[str, dict[str, Any]] = {}
                for journey_name in ("default_root", "alternate_root") + (("upgrade",) if args.baseline_dmg else ()):
                    journey = receipt.get("gui_acceptance", {}).get(journey_name, {})
                    gui_logs[journey_name] = {}
                    if journey_name in receipt.get("gui_reuse", {}).get("journeys", []):
                        gui_logs[journey_name]["source"] = "Historical log paths retained verbatim in prior-smoke-original.json; not reopened on this guest"
                        continue
                    for key in ("native_app_log", "replacement_app_log", "relaunch_app_log", "server_log"):
                        log_path = journey.get(key)
                        if log_path:
                            gui_logs[journey_name][key] = {"path": log_path, "tail": log_tail(Path(log_path))}
                receipt["logs"]["gui_acceptance"] = gui_logs
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
