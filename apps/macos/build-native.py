#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
"""Build one native release tree from a public SlopTank-server source checkout."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
import platform
import re
import signal
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path
from xml.etree import ElementTree

sys.path.insert(0, str(Path(__file__).resolve().parent / "notices"))
from sloptank_server_notice_metadata import _load_license_overrides, _shipped_nuget_packages
from sloptank_server_notices import _third_party_notices_text


def managed_source_binding(package: str, version: str, cache: Path, overrides: dict) -> dict:
    """Bind a shipped package to its declared or approved exact source."""
    key = package.lower()
    nuspec = cache / key / version / f"{key}.nuspec"
    if not nuspec.is_file():
        raise ValueError(f"missing source metadata for {package} {version}")
    root = ElementTree.parse(nuspec).getroot()
    repository = next((dict(node.attrib) for node in root.iter()
                       if node.tag.rsplit("}", 1)[-1] == "repository"), {})
    project_url = next(((node.text or "").strip() for node in root.iter()
                        if node.tag.rsplit("}", 1)[-1] == "projectUrl"), "")
    source = overrides.get(f"pkg:nuget/{package}@{version}".casefold())
    if source is None:
        url = repository.get("url") or project_url
        if url == "https://go.microsoft.com/fwlink/?linkid=868515":
            url = "https://github.com/mono/SkiaSharp"
        commit = repository.get("commit", "")
        if not url.startswith("https://") or not re.fullmatch(r"[0-9a-fA-F]{40}", commit):
            raise ValueError(f"missing exact source binding for {package} {version}")
        source = {"repository": url, "commit": commit,
                  "binding": "package-commit" if repository.get("url") else "project-url-and-package-commit"}
    return {"name": package, "version": version, "vendorRepository": repository,
            "vendorProjectUrl": project_url or None, "source": source}


def write_managed_notices(server: Path, cache: Path, corpus: Path, overrides: Path) -> None:
    """Write notices from the actual server and converter dependency manifests."""
    resolved = _load_license_overrides(overrides)
    for directory in (server, server / "svg-rasterizer"):
        notices = _third_party_notices_text(
            _shipped_nuget_packages(directory), cache, corpus, resolved,
            title=("SlopTank server - Third-Party Notices" if directory == server
                   else "Standalone SVG converter - Third-Party Notices"))
        (directory / "THIRD-PARTY-NOTICES.txt").write_text(notices, encoding="utf-8")


def stage_managed_notices(server: Path) -> None:
    """Use restored packages and official exact-version SDK runtime packages."""
    origin = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    packages = set(_shipped_nuget_packages(server) + _shipped_nuget_packages(server / "svg-rasterizer"))
    with tempfile.TemporaryDirectory(prefix="sloptank-native-notices-") as name:
        scratch = Path(name)
        with tarfile.open(Path(__file__).parent / "license-inputs.tar.gz", "r:gz") as archive:
            archive.extractall(scratch, filter="data")
        cache = scratch / "packages"
        for package, version in sorted(packages):
            key = package.lower()
            destination = cache / key / version
            destination.parent.mkdir(parents=True, exist_ok=True)
            existing = origin / key / version
            if existing.is_dir():
                destination.symlink_to(existing, target_is_directory=True)
            elif not key.startswith(("microsoft.netcore.app.runtime.", "microsoft.aspnetcore.app.runtime.")):
                raise RuntimeError(f"missing restored NuGet package {package} {version}")
            else:
                url = f"https://api.nuget.org/v3-flatcontainer/{key}/{version}/{key}.{version}.nupkg"
                with urllib.request.urlopen(url, timeout=120) as response:
                    payload = response.read()
                print(json.dumps({"nuget": package, "version": version,
                                  "sha256": hashlib.sha256(payload).hexdigest()}), flush=True)
                package_zip = scratch / "runtime.nupkg"
                package_zip.write_bytes(payload)
                destination.mkdir()
                with zipfile.ZipFile(package_zip) as archive:
                    archive.extractall(destination)
            if not key.startswith(("microsoft.netcore.app.runtime.", "microsoft.aspnetcore.app.runtime.")):
                continue
            nuspec = destination / f"{key}.nuspec"
            if not nuspec.exists():
                matches = list(destination.glob("*.nuspec"))
                if len(matches) != 1 or existing.is_dir():
                    raise RuntimeError(f"missing runtime nuspec for {package} {version}")
                matches[0].rename(nuspec)
            metadata = ElementTree.parse(nuspec).getroot()
            values = {node.tag.rsplit("}", 1)[-1]: (node.text or "").strip()
                      for node in metadata.iter() if node.tag.rsplit("}", 1)[-1] in {"id", "version"}}
            if values.get("id", "").casefold() != key or values.get("version") != version:
                raise RuntimeError(f"runtime NuGet identity mismatch for {package} {version}")
        write_managed_notices(server, cache, scratch / "license-texts", scratch / "overrides.json")
        source_overrides = json.loads((scratch / "source-overrides.json").read_text(encoding="utf-8"))
        bindings = [managed_source_binding(package, version, cache, source_overrides)
                    for package, version in sorted(packages)]
        (server / "source-locations.json").write_text(
            json.dumps({"packages": bindings}, indent=2) + "\n", encoding="utf-8")
        shutil.copy2(scratch / "sqlite-build-options.txt", server / "sqlite-build-options.txt")


def stage_preparation_licenses(preparation: Path) -> list[dict[str, str]]:
    """Include the running Python and installed bootloader's exact-version terms."""
    versions = (("PYTHON-LICENSE.txt", "python/cpython", platform.python_version(), "LICENSE"),
                ("PYINSTALLER-LICENSE.txt", "pyinstaller/pyinstaller",
                 importlib.metadata.version("pyinstaller"), "COPYING.txt"))
    records = []
    for filename, repository, version, source in versions:
        url = f"https://raw.githubusercontent.com/{repository}/v{version}/{source}"
        with urllib.request.urlopen(url, timeout=30) as response:
            content = response.read()
        if not content:
            raise RuntimeError(f"empty license from {url}")
        (preparation / filename).write_bytes(content)
        record = {"license": filename, "version": version, "source": url,
                  "sha256": hashlib.sha256(content).hexdigest()}
        records.append(record)
        print(json.dumps(record), flush=True)
    return records


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
    stage_managed_notices(output / "Server")
    sources = sorted(str(path) for path in (root / "apps/macos/Sources/SlopTank").glob("*.swift"))
    run(["xcrun", "swiftc", "-O", "-whole-module-optimization", "-target",
         f"{expected_machine}-apple-macosx12.0", *sources, "-o", str(output / "SlopTank")], root, 1200)
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
         "--distpath", str(output), "--workpath", str(output / "pyinstaller-work"),
         "apps/preparation/playback-preparation.spec"], root, 1200)
    versions = subprocess.check_output([sys.executable, "-m", "pip", "freeze"], text=True, timeout=30)
    license_sources = stage_preparation_licenses(output / "Preparation")
    (output / "Preparation/build-environment.txt").write_text(
        sys.version + "\n" + versions + "\nlicense-sources=" + json.dumps(license_sources) + "\n")
    run([sys.executable, "apps/macos/fetch-components.py",
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
