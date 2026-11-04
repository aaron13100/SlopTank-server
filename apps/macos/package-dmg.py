#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Create a local macOS disk image from a completed SlopTank.app.

Signing, notarization and release qualification must precede public distribution.
The image installs by dragging the app to Applications and never touches user data.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def package(app: Path, output: Path, timeout_seconds: float) -> None:
    """Copy the completed app and Applications shortcut into a read-only UDZO image."""
    if app.name != "SlopTank.app" or not (app / "Contents" / "Info.plist").is_file():
        raise ValueError(f"Expected a completed SlopTank.app: {app}")
    if (app / ".assembly-incomplete").exists():
        raise ValueError(f"Application assembly is incomplete: {app}")
    if output.suffix != ".dmg" or output.exists() or output.is_symlink():
        raise ValueError(f"Expected a new .dmg output: {output}")
    if timeout_seconds <= 0:
        raise ValueError("The disk-image timeout must be positive")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".sloptank-dmg-", dir=output.parent) as temporary:
        staging = Path(temporary)
        shutil.copytree(app, staging / "SlopTank.app", symlinks=True)
        (staging / "Applications").symlink_to("/Applications", target_is_directory=True)
        (staging / "Read Me.txt").write_text(
            "SlopTank for macOS\n\n"
            "Install: drag SlopTank to Applications, then open SlopTank.\n"
            "Choose Start SlopTank in the welcome window. Your browser opens setup.\n"
            "Upgrade: quit SlopTank from its menu, replace the app, then open it.\n"
            "Recovery: open SlopTank again. The same server profile is retained.\n"
            "If startup fails, use Retry and Open Server Log in the status window.\n"
            "Remove: quit SlopTank, then move the app to the Trash.\n"
            "Configuration and media remain in place during upgrade and removal.\n"
            "Reinstalling the app reuses the retained profile.\n",
            encoding="utf-8",
        )
        try:
            # Keep the child in this job's process group so an outer cancellation
            # reaches it too. run() also kills and reaps it on timeout/interruption.
            subprocess.run([
                "/usr/bin/hdiutil", "create", "-volname", "SlopTank", "-fs", "HFS+",
                "-format", "UDZO", "-srcfolder", str(staging), str(output),
            ], check=True, timeout=timeout_seconds)
        except subprocess.TimeoutExpired as error:
            raise TimeoutError(f"Disk image creation exceeded {timeout_seconds}s: {output}") from error


def main() -> int:
    """Package explicit build output; refuse overwrites and incomplete applications."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=1200)
    arguments = parser.parse_args()
    try:
        package(arguments.app.resolve(), arguments.output.absolute(), arguments.timeout_seconds)
    except Exception as error:
        print(f"Disk image packaging failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    print(arguments.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
