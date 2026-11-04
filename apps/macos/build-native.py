#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Build one native release tree from a public SlopTank-server source checkout."""

from __future__ import annotations

import argparse
import json
import os
import platform
import signal
import shutil
import subprocess
import sys
from pathlib import Path


def run(command: list[str], cwd: Path, timeout: int) -> None:
    """Run a normal-priority build with a fixed deadline (twice its expected duration)."""
    print(json.dumps({"command": command, "cwd": str(cwd), "timeoutSeconds": timeout}), flush=True)
    process = subprocess.Popen(command, cwd=cwd, start_new_session=True)
    try:
        status = process.wait(timeout=timeout - 5)
    except BaseException:
        # Stop compiler/helper children too, within the command's fixed deadline.
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            process.poll()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        raise
    if status:
        raise subprocess.CalledProcessError(status, command)


def build(output: Path, rid: str) -> None:
    """Publish complete self-contained .NET/SVG and Python trees and a launcher slice."""
    root = Path(__file__).resolve().parents[2]
    expected_machine = {"osx-x64": "x86_64", "osx-arm64": "arm64"}[rid]
    if platform.machine() != expected_machine:
        raise ValueError(f"native_runner_required: {rid} requires {expected_machine}, received {platform.machine()}")
    output.mkdir(parents=True, exist_ok=False)
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    run([dotnet, "publish", "Jellyfin.Server/Jellyfin.Server.csproj", "-c", "Release",
         "-r", rid, "--self-contained", "true", "--disable-build-servers", "-m:2",
         "-o", str(output / "Server")], root, 2400)
    sources = sorted(str(path) for path in (root / "apps/macos/Sources/SlopTank").glob("*.swift"))
    run(["xcrun", "swiftc", "-O", "-whole-module-optimization", "-target",
         f"{expected_machine}-apple-macosx12.0", *sources, "-o", str(output / "SlopTank")], root, 1200)
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
         "--distpath", str(output), "--workpath", str(output / "pyinstaller-work"),
         "apps/preparation/playback-preparation.spec"], root, 1200)
    versions = subprocess.check_output([sys.executable, "-m", "pip", "freeze"], text=True, timeout=30)
    (output / "Preparation/build-environment.txt").write_text(sys.version + "\n" + versions)
    run([sys.executable, "apps/macos/fetch-components.py", "--rid", rid,
         "--output", str(output / "upstream")], root, 600)
    for component in ("jellyfin", "svg-rasterizer/svg-rasterizer"):
        run(["lipo", str(output / "Server" / component), "-verify_arch", expected_machine], root, 30)
    run(["lipo", str(output / "Preparation/sloptank-prepare"), "-verify_arch", expected_machine], root, 30)


def main() -> None:
    """Accept a native target and new output directory, keeping build products isolated."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=("osx-x64", "osx-arm64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build(args.output.resolve(), args.rid)


if __name__ == "__main__":
    main()
