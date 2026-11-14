# SlopTank modification notice: added or changed by SlopTank on 2026-11-14.
"""Guest-only welcome controls and browser invocation for installed-app smoke.

The product app is never modified. Failure capture runs before the driver's
normal owned-process cleanup and must never replace the original failure.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

from smoke_deadline import Deadline, SmokeError, run


def write_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)



def run_welcome_ui(app_pid: int, app_bundle: Path, home: Path, action: str,
                   work: Path, deadline: Deadline, selected_root: Path | None = None) -> None:
    """Use macOS accessibility only against the recorded launcher PID."""
    config_path = work / "welcome-ui-config.json"
    write_json(config_path, {"pid": app_pid, "bundle": str(app_bundle), "home": str(home),
                             "action": action, "selectedRoot": str(selected_root) if selected_root else None,
                             "diagnosticsPath": str(work / "welcome-ui-diagnostics.json")})
    config_path.chmod(0o600)
    script_path = Path(__file__).with_name("welcome-ui.js")
    try:
        run(["/usr/bin/osascript", "-l", "JavaScript", str(script_path), str(config_path)],
            f"Run {action} against SlopTank PID {app_pid}", deadline, timeout=120)
    except BaseException as original:
        capture = {"original_error": str(original), "app_pid": app_pid}
        if os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_OS") == "macOS":
            try:
                run(["/usr/sbin/screencapture", "-x", str(work / "welcome-ui-failure.png")],
                    "Capture the authorized guest while its owned app is alive", deadline, timeout=10)
                capture["screenshot"] = str(work / "welcome-ui-failure.png")
            except Exception as error:
                capture["screenshot_error"] = f"{type(error).__name__}: {error}"
        else:
            capture["screenshot_error"] = "Capture restricted to the authorized GitHub macOS guest"
        try:
            write_json(work / "welcome-ui-failure.json", capture)
        except OSError as error:
            print(f"Could not retain welcome failure capture: {error}; original: {original}", file=sys.stderr)
        raise
    finally:
        config_path.unlink(missing_ok=True)



def _run_browser_wizard(base_url: str, media_directory: Path, media: Path, library_name: str,
                        admin_name: str, admin_password: str, node: str, work: Path,
                        environment: dict[str, str], deadline: Deadline) -> dict[str, Any]:
    module_dir = os.environ.get("SLOPTANK_PLAYWRIGHT_NODE_MODULES")
    if not module_dir or not Path(module_dir).is_dir():
        raise SmokeError("Pinned Playwright module directory is missing; interactive acceptance cannot continue.")
    config_path = work / "browser-config.json"
    write_json(config_path, {"baseUrl": base_url, "mediaDirectory": str(media_directory),
                             "mediaName": media.name, "mediaTitle": media.stem, "mediaPath": str(media),
                             "libraryName": library_name, "adminName": admin_name,
                             "adminPassword": admin_password, "workDir": str(work)})
    config_path.chmod(0o600)
    try:
        result = run([node, str(Path(__file__).with_name("smoke-browser-ui.cjs")), str(config_path)],
                     "Complete browser setup, add the scratch library and decode its video", deadline,
                     timeout=600, env=environment)
    finally:
        config_path.unlink(missing_ok=True)
    receipt_path = work / "browser-journey.json"
    try:
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise SmokeError(f"Browser driver did not write a valid receipt (exit {result.returncode}): {error}") from error
    if result.returncode or receipt.get("status") != "passed":
        raise SmokeError(f"Browser acceptance failed: {result.stderr[-2000:]} {receipt.get('error', '')}")
    return receipt

