#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Retain public component source archives and notices beside candidate binaries."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import urllib.request
from pathlib import Path


def collect(output: Path, web_ref: str) -> None:
    """Record exact application sources and upstream source locations for review."""
    sources = {
        "intro-skipper-source.tar.gz": "https://github.com/intro-skipper/intro-skipper/archive/6e0cb179007ac4c16cd9f358e9a617e791e9bf06.tar.gz",
        "ffmpeg-source.tar.gz": "https://github.com/jellyfin/jellyfin-ffmpeg/archive/refs/tags/v8.1.3-2.tar.gz",
        "intro-skipper-LICENSE": "https://raw.githubusercontent.com/intro-skipper/intro-skipper/6e0cb179007ac4c16cd9f358e9a617e791e9bf06/LICENSE",
    }
    for name, url in sources.items():
        with urllib.request.urlopen(url, timeout=120) as source, (output / name).open("xb") as target:
            shutil.copyfileobj(source, target)
    server_ref = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True, timeout=30).strip()
    (output / "sources.json").write_text(json.dumps({
        "serverAndInstaller": {"repository": "https://github.com/aaron13100/SlopTank-server", "commit": server_ref},
        "web": {"repository": "https://github.com/aaron13100/SlopTank", "commit": web_ref},
        "upstream": sources,
    }, indent=2) + "\n")
    (output / "CANDIDATE.txt").write_text(
        "Workflow artifact only. Ad-hoc signed for native execution; not Developer ID signed or notarized.\n"
        "Runtime-pack notices are retained inside each self-contained Server and SVG tree.\n"
        "Preparation dependencies and Python notices remain inside Preparation/_internal.\n"
        "Public release still requires exact-component licence/source clearance, signing and notarization.\n"
    )


def main() -> None:
    """Add release materials to an existing assembly directory."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--web-ref", required=True)
    args = parser.parse_args()
    collect(args.output, args.web_ref)


if __name__ == "__main__":
    main()
