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
import tempfile
import urllib.request
from pathlib import Path


FFMPEG_VERSION = "8.1.3-2"
FFMPEG_SOURCE_COMMIT = "7250de8375a4959ac1c25c139ce9789fdac199fb"
FFMPEG_ZVBI_COMMIT = "41477c97c8edf7a01f1594b2a95b94f0117eed21"
FFMPEG_PATCH_SHA256 = "f1c52398f7e093c5c2d0d5abd1de91b4d96bcaba983f8649d548cb4d7a200187"
FFMPEG_LEGACY_WORKFLOW_COMMIT = "5bf1d9368240ad75699c4cf597763b6e5ae45af0"
FFMPEG_LEGACY_WORKFLOW_SHA256 = "ff86fbe4ed7d82df9206682deae65814743dfabbdf86ef6899025d384f73d653"
FFMPEG_MACOS_RECIPE_PATCH = "ffmpeg-macos-no-gcc.patch"
FFMPEG_RUN_REPOSITORY = "https://github.com/aaron13100/SlopTank-server"
FFMPEG_SOURCE_REPOSITORY = "https://github.com/jellyfin/jellyfin-ffmpeg"

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


def _sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _source_facts(path: Path) -> dict[str, str]:
    facts = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        key, separator, value = line.partition("=")
        if not separator or not key or key in facts:
            raise ValueError(f"invalid FFmpeg source fact: {path}")
        facts[key] = value
    return facts


def _binary_hashes(path: Path) -> dict[str, str]:
    hashes = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        digest, separator, filename = line.partition("  ")
        name = Path(filename.lstrip("*")).name
        if not separator or not re.fullmatch(r"[0-9a-f]{64}", digest) or name in hashes:
            raise ValueError(f"invalid FFmpeg binary hash record: {path}")
        hashes[name] = digest
    if set(hashes) != {"ffmpeg", "ffprobe"}:
        raise ValueError(f"expected ffmpeg and ffprobe hashes: {path}")
    return hashes


def stage_ffmpeg_component(rid: str, artifact: Path, runtime: Path, run_id: str) -> dict:
    """Verify a source-built component artifact and stage its exact binaries for one RID."""
    targets = {"osx-x64": ("x86_64", "mac64"), "osx-arm64": ("arm64", "macarm64")}
    if rid not in targets:
        raise ValueError(f"unsupported FFmpeg runtime: {rid}")
    if not artifact.is_dir():
        raise ValueError(f"missing FFmpeg build artifact for {rid}: {artifact}")
    if not re.fullmatch(r"[1-9][0-9]*", str(run_id)):
        raise ValueError("FFmpeg source-build run ID must be a positive integer")
    arch, target = targets[rid]
    facts = _source_facts(artifact / "source-facts.txt")
    expected_facts = {
        "source_commit": FFMPEG_SOURCE_COMMIT,
        "zvbi_commit": FFMPEG_ZVBI_COMMIT,
        "architecture": arch,
        "target": target,
    }
    if any(facts.get(key) != value for key, value in expected_facts.items()):
        raise ValueError(f"FFmpeg source or architecture metadata mismatch for {rid}")
    patch = artifact / "sources" / "ffmpeg-zvbi-subtitles-only.patch"
    if not patch.is_file() or _sha256(patch) != FFMPEG_PATCH_SHA256:
        raise ValueError(f"FFmpeg teletext patch is missing or changed for {rid}")
    name = f"jellyfin-ffmpeg_{FFMPEG_VERSION}_portable_{target}-gpl.tar.xz"
    package = artifact / name
    if not package.is_file():
        raise ValueError(f"missing built FFmpeg archive for {rid}: {package}")
    binary_hashes = _binary_hashes(artifact / "binaries.sha256")
    for filename, expected in binary_hashes.items():
        path = artifact / filename
        if not path.is_file() or _sha256(path) != expected:
            raise ValueError(f"FFmpeg binary SHA-256 mismatch for {rid}: {filename}")
    workflow_commit = facts.get("workflow_commit", FFMPEG_LEGACY_WORKFLOW_COMMIT)
    workflow_sha = facts.get("workflow_sha256", FFMPEG_LEGACY_WORKFLOW_SHA256)
    workflow_path = artifact / "sources" / "ffmpeg-subtitles.yml"
    if workflow_path.is_file() and _sha256(workflow_path) != workflow_sha:
        raise ValueError(f"FFmpeg build workflow SHA-256 mismatch for {rid}")
    if not re.fullmatch(r"[0-9a-f]{40}", workflow_commit) or not re.fullmatch(r"[0-9a-f]{64}", workflow_sha):
        raise ValueError(f"invalid FFmpeg build workflow metadata for {rid}")
    recipe_patch = artifact / "sources" / FFMPEG_MACOS_RECIPE_PATCH
    recipe_patch_sha = facts.get("macos_recipe_patch_sha256")
    if recipe_patch_sha:
        if not recipe_patch.is_file() or _sha256(recipe_patch) != recipe_patch_sha:
            raise ValueError(f"FFmpeg macOS recipe patch mismatch for {rid}")
    elif rid == "osx-x64":
        raise ValueError(f"missing FFmpeg macOS recipe patch for {rid}")
    try:
        with tarfile.open(package, "r:xz") as source:
            members = source.getmembers()
            if (len(members) != 2 or {member.name for member in members} != {"ffmpeg", "ffprobe"}
                    or any(not member.isfile() for member in members)):
                raise ValueError(f"unexpected FFmpeg archive contents for {rid}")
            with tempfile.TemporaryDirectory(prefix="ffmpeg-stage-", dir=runtime) as scratch_name:
                extracted = Path(scratch_name)
                source.extractall(extracted, members=members, filter="data")
                for filename, expected in binary_hashes.items():
                    if _sha256(extracted / filename) != expected:
                        raise ValueError(f"FFmpeg archive binary SHA-256 mismatch for {rid}: {filename}")
                destination = runtime / "upstream" / "ffmpeg"
                destination.mkdir(parents=True, exist_ok=False)
                for filename in ("ffmpeg", "ffprobe"):
                    shutil.copy2(extracted / filename, destination / filename)
    except (OSError, tarfile.TarError) as error:
        raise ValueError(f"invalid built FFmpeg archive for {rid}: {error}") from error
    component = {
        "version": FFMPEG_VERSION,
        "architecture": arch,
        "archive": name,
        "archiveSha256": _sha256(package),
        "binaries": binary_hashes,
        "source": {
            "repository": FFMPEG_SOURCE_REPOSITORY,
            "commit": FFMPEG_SOURCE_COMMIT,
            "zvbiCommit": FFMPEG_ZVBI_COMMIT,
            "patch": patch.name,
            "patchSha256": FFMPEG_PATCH_SHA256,
            **({"macosRecipePatch": recipe_patch.name, "macosRecipePatchSha256": recipe_patch_sha}
               if recipe_patch_sha else {}),
        },
        "build": {
            "repository": FFMPEG_RUN_REPOSITORY,
            "workflowCommit": workflow_commit,
            "workflowSha256": workflow_sha,
            "runId": int(run_id),
            "runUrl": f"{FFMPEG_RUN_REPOSITORY}/actions/runs/{run_id}",
        },
    }
    metadata_path = runtime / "upstream" / "components.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if not isinstance(metadata.get("introSkipper"), dict) or "ffmpeg" in metadata:
        raise ValueError(f"unexpected native component metadata before FFmpeg staging: {rid}")
    metadata["ffmpeg"] = component
    metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    return component


def _ffmpeg_source_instructions(asset_name: str, source_url: str) -> str:
    return (
        "This archive contains the exact source trees, dependency pins, upstream macOS build recipes, "
        "SlopTank patches, corresponding-source offer, per-architecture hashes, toolchain facts, "
        "and build logs for the "
        f"FFmpeg {FFMPEG_VERSION} component. It does not contain the compiled FFmpeg binaries.\n\n"
        f"Expected public release asset: {asset_name}\n"
        f"Expected URL: {source_url}\n"
        "The candidate workflow artifact is not itself a published public release. Publish this source "
        "asset beside the matching DMG when that release is made.\n\n"
        f"The source checkout is Jellyfin FFmpeg commit {FFMPEG_SOURCE_COMMIT}. The exact workflow "
        "and macOS dependency recipe used for each architecture are included with that architecture's "
        "build records. Apply "
        "ffmpeg-zvbi-subtitles-only.patch to the pinned zvbi checkout after `cd zvbi` and before "
        "`./autogen.sh`, as recorded in build-script-injection.patch. On a native macOS runner, "
        "follow the included builder/Buildmac.md and run `./builder/buildmac.sh x86_64` or "
        "`./builder/buildmac.sh arm64`. The exact enabled dependency sources and immutable pins for "
        "each architecture are supplied under its sources/ directory and source-pins.tsv.\n"
    )


def collect_ffmpeg_source_kit(
    source_kit: Path,
    version: str,
    artifacts: dict[str, Path],
    runtimes: dict[str, Path],
    components: dict[str, dict],
    offer: Path,
    license_text: Path,
) -> dict:
    """Combine both architecture source inputs without bundling either built binary."""
    expected_rids = {"osx-x64", "osx-arm64"}
    if set(artifacts) != expected_rids or set(runtimes) != expected_rids or set(components) != expected_rids:
        raise ValueError("the FFmpeg corresponding-source kit requires both macOS architectures")
    asset_name = f"SlopTank-{version}-ffmpeg-corresponding-source.tar.gz"
    source_url = f"https://github.com/aaron13100/SlopTank-server/releases/download/v{version}/{asset_name}"
    if not offer.is_file() or not license_text.is_file():
        raise ValueError("missing FFmpeg source offer or GPLv3 text")
    instructions = _ffmpeg_source_instructions(asset_name, source_url)
    source_kit.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ffmpeg-source-kit-") as temporary:
        temporary_root = Path(temporary)
        offer_copy = temporary_root / "FFMPEG-SOURCE-OFFER.md"
        shutil.copy2(offer, offer_copy)
        access = temporary_root / "FFMPEG-SOURCE-ACCESS.txt"
        access.write_text(instructions, encoding="utf-8")
        with tarfile.open(source_kit, "x:gz", compresslevel=1) as kit:
            kit.add(offer_copy, arcname="FFMPEG-SOURCE-OFFER.md")
            kit.add(access, arcname="REBUILD-AND-RELINK.txt")
            kit.add(license_text, arcname="licenses/GPL-3.0.txt")
            for rid in sorted(expected_rids):
                artifact = artifacts[rid]
                source_directory = artifact / "sources"
                if not source_directory.is_dir():
                    raise ValueError(f"missing retained FFmpeg source tree for {rid}")
                root = f"ffmpeg-corresponding-source-kit/{rid}"
                kit.add(source_directory, arcname=f"{root}/sources")
                workflow_path = source_directory / "ffmpeg-subtitles.yml"
                if not workflow_path.is_file():
                    if rid != "osx-arm64":
                        raise ValueError(f"missing retained FFmpeg workflow source for {rid}")
                    result = subprocess.run(
                        ["git", "show", f"{FFMPEG_LEGACY_WORKFLOW_COMMIT}:.github/workflows/ffmpeg-subtitles.yml"],
                        check=True, capture_output=True, timeout=30)
                    if hashlib.sha256(result.stdout).hexdigest() != FFMPEG_LEGACY_WORKFLOW_SHA256:
                        raise ValueError("legacy FFmpeg build workflow source hash mismatch")
                    workflow_path = temporary_root / f"ffmpeg-subtitles-{rid}.yml"
                    workflow_path.write_bytes(result.stdout)
                if f"FF_SOURCE_COMMIT: {FFMPEG_SOURCE_COMMIT}" not in workflow_path.read_text(encoding="utf-8"):
                    raise ValueError(f"FFmpeg workflow source pin mismatch for {rid}")
                kit.add(workflow_path, arcname=f"{root}/workflow/ffmpeg-subtitles.yml")
                for filename in (
                    "source-pins.tsv", "sources.sha256", "source-facts.txt", "fixture.sha256",
                    "subtitle-summary.txt", "binaries.sha256", "build.log", "original-symbols.txt",
                    "patched-symbols.txt", "original-streams.json", "patched-streams.json",
                ):
                    path = artifact / filename
                    if not path.is_file():
                        raise ValueError(f"missing FFmpeg source-build record for {rid}: {filename}")
                    kit.add(path, arcname=f"{root}/{filename}")
                toolchain_facts = artifact / "toolchain-facts.txt"
                if toolchain_facts.is_file():
                    kit.add(toolchain_facts, arcname=f"{root}/toolchain-facts.txt")
                elif rid == "osx-x64":
                    raise ValueError("missing x86_64 FFmpeg toolchain facts")
                metadata = temporary_root / f"components-{rid}.json"
                metadata.write_text(json.dumps(components[rid], indent=2) + "\n", encoding="utf-8")
                kit.add(metadata, arcname=f"{root}/components.json")
    return {"file": asset_name, "url": source_url, "sha256": _sha256(source_kit)}


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


def collect(
    output: Path,
    web_ref: str,
    runtimes: dict[str, Path],
    ffmpeg_artifacts: dict[str, Path],
    ffmpeg_run_ids: dict[str, str],
    source_kit_output: Path,
    version: str,
) -> None:
    """Stage verified FFmpeg artifacts and record app/source-kit provenance."""
    ffmpeg_components = {
        rid: stage_ffmpeg_component(rid, ffmpeg_artifacts[rid], runtimes[rid], ffmpeg_run_ids[rid])
        for rid in sorted(runtimes)
    }
    sources = {
        "intro-skipper-source.tar.gz": "https://github.com/intro-skipper/intro-skipper/archive/6e0cb179007ac4c16cd9f358e9a617e791e9bf06.tar.gz",
        "intro-skipper-LICENSE": "https://raw.githubusercontent.com/intro-skipper/intro-skipper/6e0cb179007ac4c16cd9f358e9a617e791e9bf06/LICENSE",
    }
    for name, url in sources.items():
        with urllib.request.urlopen(url, timeout=120) as source, (output / name).open("xb") as target:
            shutil.copyfileobj(source, target)
    stage_gpl_licenses(output)
    root = Path(__file__).resolve().parents[2]
    offer = root / "FFMPEG-SOURCE-OFFER.md"
    shutil.copy2(offer, output / offer.name)
    source_kit = collect_ffmpeg_source_kit(
        source_kit_output, version, ffmpeg_artifacts, runtimes, ffmpeg_components,
        offer, output / "GPL-3.0.txt")
    (output / "FFMPEG-SOURCE-ACCESS.txt").write_text(
        _ffmpeg_source_instructions(source_kit["file"], source_kit["url"]), encoding="utf-8")
    server_ref = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True, timeout=30).strip()
    managed_sources, managed_archives = collect_managed_sources(output, runtimes)
    (output / "sources.json").write_text(json.dumps({
        "serverAndInstaller": {"repository": "https://github.com/aaron13100/SlopTank-server", "commit": server_ref},
        "web": {"repository": "https://github.com/aaron13100/SlopTank", "commit": web_ref},
        "upstream": sources,
        "ffmpegComponents": ffmpeg_components,
        "ffmpegCorrespondingSourceKit": source_kit,
        "preparationSources": collect_preparation_sources(output, list(runtimes.values())),
        "managedSources": managed_sources,
        "managedSourceArchives": managed_archives,
    }, indent=2) + "\n")
    (output / "CANDIDATE.txt").write_text(
        "Workflow artifact only. Ad-hoc signed for native execution; not Developer ID signed or notarized.\n"
        "Runtime-pack notices are retained inside each self-contained Server and SVG tree.\n"
        "Preparation dependency notices and exact-version source archives accompany the app.\n"
        "The FFmpeg corresponding-source kit is a separate release asset; see FFMPEG-SOURCE-ACCESS.txt.\n"
        "Public release still requires exact-component licence/source clearance, signing and notarization.\n"
    )


def main() -> None:
    """Add release materials to an existing assembly directory."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--web-ref", required=True)
    parser.add_argument("--runtime", action="append", required=True, metavar="RID=DIR")
    parser.add_argument("--ffmpeg-component", action="append", required=True, metavar="RID=DIR")
    parser.add_argument("--ffmpeg-run-id", action="append", required=True, metavar="RID=ID")
    parser.add_argument("--source-kit-output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    runtimes = {}
    for value in args.runtime:
        rid, separator, directory = value.partition("=")
        if not separator or not re.fullmatch(r"osx-(?:x64|arm64)", rid) or not directory:
            parser.error("--runtime must be osx-x64=DIR or osx-arm64=DIR")
        if rid in runtimes:
            parser.error(f"duplicate runtime {rid}")
        runtimes[rid] = Path(directory)
    ffmpeg_artifacts = {}
    for value in args.ffmpeg_component:
        rid, separator, directory = value.partition("=")
        if not separator or not re.fullmatch(r"osx-(?:x64|arm64)", rid) or not directory:
            parser.error("--ffmpeg-component must be osx-x64=DIR or osx-arm64=DIR")
        if rid in ffmpeg_artifacts:
            parser.error(f"duplicate FFmpeg component {rid}")
        ffmpeg_artifacts[rid] = Path(directory)
    ffmpeg_run_ids = {}
    for value in args.ffmpeg_run_id:
        rid, separator, run_id = value.partition("=")
        if not separator or not re.fullmatch(r"osx-(?:x64|arm64)", rid) or not run_id:
            parser.error("--ffmpeg-run-id must be osx-x64=ID or osx-arm64=ID")
        if rid in ffmpeg_run_ids:
            parser.error(f"duplicate FFmpeg source-build run ID {rid}")
        ffmpeg_run_ids[rid] = run_id
    collect(args.output, args.web_ref, runtimes, ffmpeg_artifacts,
            ffmpeg_run_ids, args.source_kit_output, args.version)


if __name__ == "__main__":
    main()
