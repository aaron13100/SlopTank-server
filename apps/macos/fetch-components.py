#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
"""Fetch pinned public native components, retaining hashes and upstream materials."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import urllib.request
import zipfile
from pathlib import Path


INTRO_VERSION = "12.0.4.0"
INTRO_SHA256 = "11d7bb171b66a4faa4abb0d9af92d2e9e6ae3ebd6a821bb5aca1d94bff651439"


def download(url: str, destination: Path, digest: str) -> None:
    """Download a pinned archive and reject any unexpected payload before extraction."""
    with urllib.request.urlopen(url, timeout=120) as response, destination.open("xb") as output:
        shutil.copyfileobj(response, output)
    actual = hashlib.sha256(destination.read_bytes()).hexdigest()
    if actual != digest:
        raise ValueError(f"component_hash_mismatch: {url}: expected {digest}, received {actual}")


def fetch(output: Path) -> None:
    """Extract the shared managed plugin; FFmpeg is supplied by the pinned source build."""
    output.mkdir(parents=True, exist_ok=False)
    plugin_url = f"https://github.com/intro-skipper/intro-skipper/releases/download/12.0/v{INTRO_VERSION}/intro-skipper-v{INTRO_VERSION}.zip"
    plugin_archive = output / "intro-skipper.zip"
    download(plugin_url, plugin_archive, INTRO_SHA256)
    plugin = output / "IntroSkipper"
    with zipfile.ZipFile(plugin_archive) as source:
        source.extractall(plugin)
    (plugin / "meta.json").write_text(json.dumps({
        "guid": "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b", "name": "Intro Skipper",
        "version": INTRO_VERSION, "targetAbi": "12.0.0.0", "status": 0, "assemblies": [],
    }, indent=2) + "\n")
    (output / "components.json").write_text(json.dumps({
        "introSkipper": {"url": plugin_url, "sha256": INTRO_SHA256},
    }, indent=2) + "\n")


def main() -> None:
    """Require a new explicit output directory and a known public native RID."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    fetch(args.output)


if __name__ == "__main__":
    main()
