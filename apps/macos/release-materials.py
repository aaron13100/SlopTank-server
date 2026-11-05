#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
"""Retain public component source archives and notices beside candidate binaries."""

from __future__ import annotations

import argparse
import email.parser
import hashlib
import json
import re
import shutil
import subprocess
import urllib.request
from pathlib import Path


def preparation_source_requirements(runtimes: list[Path]) -> dict:
    """Collect actual installed package and build-interpreter versions."""
    packages: set[tuple[str, str]] = set()
    python_versions: set[str] = set()
    for runtime in runtimes:
        preparation = runtime / "Preparation"
        internal = preparation / "_internal"
        if not internal.is_dir():
            raise ValueError(f"missing Preparation/_internal: {runtime}")
        metadata_dirs = list(internal.rglob("*.dist-info"))
        if not metadata_dirs:
            raise ValueError(f"no distribution METADATA found: {runtime}")
        for directory in metadata_dirs:
            metadata_path = directory / "METADATA"
            if not metadata_path.is_file():
                raise ValueError(f"missing METADATA: {directory}")
            metadata = email.parser.Parser().parsestr(metadata_path.read_text(encoding="utf-8"))
            name, version = metadata.get("Name"), metadata.get("Version")
            if not name or not version:
                raise ValueError(f"missing name or version in METADATA: {metadata_path}")
            packages.add((re.sub(r"[-_.]+", "-", name).lower(), version))
        lines = (preparation / "build-environment.txt").read_text(encoding="utf-8").splitlines()
        entries = [line.removeprefix("license-sources=") for line in lines
                   if line.startswith("license-sources=")]
        if len(entries) != 1:
            raise ValueError(f"missing preparation license source versions: {runtime}")
        records = json.loads(entries[0])
        versions = {item["license"]: item["version"] for item in records}
        python_versions.add(versions["PYTHON-LICENSE.txt"])
        packages.add(("pyinstaller", versions["PYINSTALLER-LICENSE.txt"]))
    if not runtimes:
        raise ValueError("at least one preparation runtime is required")
    return {"python": python_versions, "packages": packages}


def verify_source_checksum(archive: Path, expected: str) -> None:
    """Refuse any source archive whose bytes disagree with PyPI metadata."""
    with archive.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != expected:
        raise ValueError(f"source archive SHA-256 mismatch: {archive.name}")


def _download(url: str, target: Path) -> str:
    if not url.startswith("https://"):
        raise ValueError(f"source URL must use HTTPS: {url}")
    with urllib.request.urlopen(url, timeout=120) as response, target.open("xb") as output:
        shutil.copyfileobj(response, output)
    with target.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _collect_pypi_source(source_dir: Path, name: str, version: str) -> dict:
    """Fetch the official exact-version source distribution and verify it."""
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", name) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.+!_-]*", version):
        raise ValueError(f"invalid package identity: {name} {version}")
    with urllib.request.urlopen(f"https://pypi.org/pypi/{name}/{version}/json", timeout=30) as response:
        document = json.load(response)
    info = document["info"]
    if re.sub(r"[-_.]+", "-", info["name"]).lower() != name or info["version"] != version:
        raise ValueError(f"PyPI identity mismatch: {name} {version}")
    candidates = [item for item in document["urls"] if item["packagetype"] == "sdist"]
    if len(candidates) != 1:
        raise ValueError(f"expected one source distribution: {name} {version}")
    item = candidates[0]
    filename, url, expected = item["filename"], item["url"], item["digests"]["sha256"]
    if Path(filename).name != filename or not re.fullmatch(r"[0-9a-f]{64}", expected):
        raise ValueError(f"invalid source archive metadata: {name} {version}")
    target = source_dir / filename
    _download(url, target)
    verify_source_checksum(target, expected)
    return {"name": name, "version": version, "file": filename,
            "url": url, "sha256": expected}

def collect_preparation_sources(output: Path, runtimes: list[Path]) -> list[dict]:
    """Retain exact CPython and PyPI source archives beside the app."""
    required = preparation_source_requirements(runtimes)
    source_dir = output / "preparation-sources"
    source_dir.mkdir()
    records = []
    for version in sorted(required["python"]):
        if not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ValueError(f"invalid CPython version: {version}")
        filename = f"Python-{version}.tar.xz"
        url = f"https://www.python.org/ftp/python/{version}/{filename}"
        digest = _download(url, source_dir / filename)
        records.append({"name": "CPython", "version": version, "file": filename,
                        "url": url, "sha256": digest})
    for name, version in sorted(required["packages"]):
        records.append(_collect_pypi_source(source_dir, name, version))
    return records


def collect(output: Path, web_ref: str, runtimes: list[Path]) -> None:
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
        "preparationSources": collect_preparation_sources(output, runtimes),
    }, indent=2) + "\n")
    (output / "CANDIDATE.txt").write_text(
        "Workflow artifact only. Ad-hoc signed for native execution; not Developer ID signed or notarized.\n"
        "Runtime-pack notices are retained inside each self-contained Server and SVG tree.\n"
        "Preparation dependency notices and exact-version source archives accompany the app.\n"
        "Public release still requires exact-component licence/source clearance, signing and notarization.\n"
    )


def main() -> None:
    """Add release materials to an existing assembly directory."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--web-ref", required=True)
    parser.add_argument("--runtime", action="append", required=True, metavar="RID=DIR")
    args = parser.parse_args()
    runtimes = []
    for value in args.runtime:
        rid, separator, directory = value.partition("=")
        if not separator or not re.fullmatch(r"osx-(?:x64|arm64)", rid) or not directory:
            parser.error("--runtime must be osx-x64=DIR or osx-arm64=DIR")
        runtimes.append(Path(directory))
    collect(args.output, args.web_ref, runtimes)


if __name__ == "__main__":
    main()
