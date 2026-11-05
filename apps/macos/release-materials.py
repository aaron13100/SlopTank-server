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
import tarfile
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


def stage_gpl_licenses(output: Path) -> None:
    """Retain the complete standalone GPL texts from the audited corpus."""
    with tarfile.open(Path(__file__).parent / "license-inputs.tar.gz", "r:gz") as archive:
        for version in ("2.0", "3.0"):
            member = archive.extractfile(f"license-texts/GPL-{version}-or-later.txt")
            if member is None:
                raise ValueError(f"missing GPL-{version} license text")
            (output / f"GPL-{version}.txt").write_bytes(member.read())


def collect_managed_sources(output: Path, runtimes: dict[str, Path]) -> tuple[list[dict], list[dict]]:
    """Retain actual source bindings and the LGPL replacement materials."""
    graphs = []
    packages = {}
    for rid, runtime in sorted(runtimes.items()):
        server = runtime / "Server"
        data = json.loads((server / "source-locations.json").read_text(encoding="utf-8"))
        if not isinstance(data.get("packages"), list) or not data["packages"]:
            raise ValueError(f"missing managed source bindings for {rid}")
        graphs.append({"rid": rid, "packages": data["packages"]})
        for item in data["packages"]:
            key = (item["name"].casefold(), item["version"])
            if key in packages and packages[key]["source"] != item["source"]:
                raise ValueError(f"conflicting managed source binding for {item['name']} {item['version']}")
            packages[key] = item
    kit = output / "managed-source-kit"
    kit.mkdir()
    archives = []
    for name, version, repository, commit in (
        ("BDInfo", "0.8.0", "jellyfin/BDInfo", "40d2c5be76f68096f7277b706ccaf23395ee6e6e"),
        ("TagLibSharp", "2.3.0", "mono/taglib-sharp", "b5ae84f2e84087bf160bb0471420200dd2b5d809"),
    ):
        item = packages.get((name.casefold(), version))
        if item is None or item["source"].get("commit") != commit:
            raise ValueError(f"missing approved LGPL source binding for {name} {version}")
        filename = f"{name}-{version}-{commit}.tar.gz"
        url = f"https://codeload.github.com/{repository}/tar.gz/{commit}"
        archives.append({"name": name, "version": version, "file": f"managed-source-kit/{filename}",
                         "url": url, "sha256": _download(url, kit / filename)})
    sqlite = packages.get(("sqlitepclraw.lib.e_sqlite3", "2.1.13"))
    if sqlite is None:
        raise ValueError("missing SQLite native source binding")
    source = sqlite["source"]
    sqlite_file = kit / "sqlite-amalgamation-3530300.zip"
    _download(source["archive"], sqlite_file)
    verify_source_checksum(sqlite_file, source["sha256"])
    archives.append({"name": "SQLite", "version": "3.53.3", "file": f"managed-source-kit/{sqlite_file.name}",
                     "url": source["archive"], "sha256": source["sha256"],
                     "sourceId": source["sourceId"]})
    option_files = [(runtime / "Server/sqlite-build-options.txt").read_bytes()
                    for runtime in runtimes.values()]
    if any(value != option_files[0] for value in option_files[1:]):
        raise ValueError("runtime SQLite build options differ")
    (kit / "sqlite-build-options.txt").write_bytes(option_files[0])
    with tarfile.open(Path(__file__).parent / "license-inputs.tar.gz", "r:gz") as archive:
        for license_name in ("LGPL-2.1-only", "LGPL-3.0-only"):
            member = archive.extractfile(f"license-texts/{license_name}.txt")
            if member is None:
                raise ValueError(f"missing {license_name} license text")
            (kit / f"{license_name}.txt").write_bytes(member.read())
    (kit / "REBUILD-AND-RELINK.txt").write_text(
        "Each managed source entry identifies its exact repository and commit or archive. "
        "For Git sources use git clone, git checkout <commit>, and git submodule update --init --recursive.\n"
        "Extract the supplied BDInfo and TagLibSharp archives. From each extracted root, "
        "build src/BDInfo/BDInfo.csproj and src/TaglibSharp/TaglibSharp.csproj respectively "
        "with a compatible .NET SDK. On a copy of the app, replace BDInfo.dll or "
        "TagLibSharp.dll under Contents/Resources/Runtimes/<rid>/Server for the target "
        "architecture, re-sign the copy, and test with isolated state. You may modify "
        "these libraries and reverse engineer the combined work to debug your modifications.\n"
        "SQLite source ID and observed compile options are supplied. Build the SQLite "
        "amalgamation as libe_sqlite3 for the target architecture; the original native "
        "package build invocation is unknown.\n", encoding="utf-8")
    return graphs, archives


def collect(output: Path, web_ref: str, runtimes: dict[str, Path]) -> None:
    """Record exact application sources and upstream source locations for review."""
    sources = {
        "intro-skipper-source.tar.gz": "https://github.com/intro-skipper/intro-skipper/archive/6e0cb179007ac4c16cd9f358e9a617e791e9bf06.tar.gz",
        "ffmpeg-source.tar.gz": "https://github.com/jellyfin/jellyfin-ffmpeg/archive/refs/tags/v8.1.3-2.tar.gz",
        "intro-skipper-LICENSE": "https://raw.githubusercontent.com/intro-skipper/intro-skipper/6e0cb179007ac4c16cd9f358e9a617e791e9bf06/LICENSE",
    }
    for name, url in sources.items():
        with urllib.request.urlopen(url, timeout=120) as source, (output / name).open("xb") as target:
            shutil.copyfileobj(source, target)
    stage_gpl_licenses(output)
    server_ref = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True, timeout=30).strip()
    managed_sources, managed_archives = collect_managed_sources(output, runtimes)
    (output / "sources.json").write_text(json.dumps({
        "serverAndInstaller": {"repository": "https://github.com/aaron13100/SlopTank-server", "commit": server_ref},
        "web": {"repository": "https://github.com/aaron13100/SlopTank", "commit": web_ref},
        "upstream": sources,
        "preparationSources": collect_preparation_sources(output, list(runtimes.values())),
        "managedSources": managed_sources,
        "managedSourceArchives": managed_archives,
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
    runtimes = {}
    for value in args.runtime:
        rid, separator, directory = value.partition("=")
        if not separator or not re.fullmatch(r"osx-(?:x64|arm64)", rid) or not directory:
            parser.error("--runtime must be osx-x64=DIR or osx-arm64=DIR")
        if rid in runtimes:
            parser.error(f"duplicate runtime {rid}")
        runtimes[rid] = Path(directory)
    collect(args.output, args.web_ref, runtimes)


if __name__ == "__main__":
    main()
