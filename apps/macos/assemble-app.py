#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Assemble a macOS SlopTank.app from already-built release components."""

from __future__ import annotations

import argparse
import json
import os
import plistlib
import re
import shutil
import stat
import sys
import tempfile
from pathlib import Path
from typing import Sequence


class AssemblyError(Exception):
    """A typed assembly failure with its source path and original cause."""

    def __init__(self, code: str, message: str, path: Path | None = None, cause: Exception | None = None):
        self.code = code
        self.path = path
        self.cause = cause
        location = f" ({path})" if path is not None else ""
        reason = f" Cause: {cause}" if cause is not None else ""
        super().__init__(f"[{code}] {message}{location}.{reason}")


def _is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _regular_file(path: Path, label: str, executable: bool = False) -> Path:
    try:
        details = path.lstat()
    except OSError as error:
        raise AssemblyError("input_file_unavailable", f"The {label} could not be inspected", path, error) from error
    if not stat.S_ISREG(details.st_mode):
        raise AssemblyError("input_file_not_regular", f"The {label} must be a regular file", path)
    if executable and details.st_mode & 0o111 == 0:
        raise AssemblyError("input_not_executable", f"The {label} must have an executable mode", path)
    try:
        return path.resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise AssemblyError("input_file_resolution_failed", f"The {label} path could not be resolved", path, error) from error


def _directory(path: Path, label: str) -> Path:
    try:
        details = path.lstat()
    except OSError as error:
        raise AssemblyError("input_directory_unavailable", f"The {label} could not be inspected", path, error) from error
    if not stat.S_ISDIR(details.st_mode):
        raise AssemblyError("input_not_directory", f"The {label} must be a regular directory", path)
    try:
        return path.resolve(strict=True)
    except (OSError, RuntimeError) as error:
        raise AssemblyError("input_directory_resolution_failed", f"The {label} path could not be resolved", path, error) from error


def _validate_tree_links(root: Path, label: str) -> None:
    def fail_walk(error: OSError) -> None:
        path = Path(error.filename) if error.filename else root
        raise AssemblyError("component_inspection_failed", f"The {label} could not be fully inspected", path, error) from error

    for directory, child_dirs, child_files in os.walk(root, followlinks=False, onerror=fail_walk):
        for name in child_dirs + child_files:
            link = Path(directory, name)
            if not link.is_symlink():
                continue
            try:
                target_text = os.readlink(link)
                if Path(target_text).is_absolute():
                    raise AssemblyError("absolute_symlink", f"The {label} contains an absolute symlink", link)
                resolved = link.resolve(strict=True)
            except AssemblyError:
                raise
            except (OSError, RuntimeError) as error:
                raise AssemblyError("invalid_symlink", f"The {label} contains a broken or invalid symlink", link, error) from error
            if not _is_within(resolved, root):
                raise AssemblyError("escaping_symlink", f"The {label} contains a symlink that escapes its directory", link)


def _copy_tree(source: Path, target: Path, label: str) -> None:
    try:
        shutil.copytree(source, target, symlinks=True, copy_function=shutil.copy2)
    except Exception as error:
        raise AssemblyError("component_copy_failed", f"Could not copy the {label}", source, error) from error


def _validate_metadata(template: Path, version: str, build_number: str) -> bytes:
    try:
        with template.open("rb") as source:
            metadata = plistlib.load(source)
    except Exception as error:
        raise AssemblyError("bundle_template_invalid", "The bundle Info.plist template could not be read", template, error) from error
    if not isinstance(metadata, dict):
        raise AssemblyError("bundle_template_invalid", "The bundle Info.plist template must contain a dictionary", template)
    if metadata.get("CFBundleExecutable") != "SlopTank" or metadata.get("CFBundleIdentifier") != "org.sloptank.desktop":
        raise AssemblyError("bundle_template_identity_invalid", "The template executable or bundle identifier is unexpected", template)
    if metadata.get("LSUIElement") is not True:
        raise AssemblyError("bundle_template_policy_invalid", "The template must preserve menu-bar app behavior", template)
    network = metadata.get("NSAppTransportSecurity")
    if not isinstance(network, dict) or network.get("NSAllowsLocalNetworking") is not True:
        raise AssemblyError("bundle_template_policy_invalid", "The template must preserve its local-networking setting", template)
    if not isinstance(metadata.get("NSLocalNetworkUsageDescription"), str):
        raise AssemblyError("bundle_template_policy_invalid", "The template must retain its local-network usage description", template)
    metadata["CFBundleShortVersionString"] = version
    metadata["CFBundleVersion"] = build_number
    return plistlib.dumps(metadata, fmt=plistlib.FMT_XML, sort_keys=False)


def _require_release_value(value: str, name: str) -> str:
    if not value or value.strip() != value or any(character in value for character in ("/", "\\", "\0", "\n", "\r")):
        raise AssemblyError("release_value_invalid", f"The supplied {name} is empty or contains invalid characters")
    return value


def _reserve_output(output: Path) -> Path:
    try:
        output.mkdir(mode=0o700)
    except FileExistsError as error:
        raise AssemblyError("output_already_exists", "The output path already exists; choose a new .app path", output, error) from error
    except OSError as error:
        raise AssemblyError("output_create_failed", "Could not exclusively create the output directory", output, error) from error
    marker = output / ".assembly-incomplete"
    try:
        descriptor = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write("Assembly is incomplete. Do not distribute this application.\n")
    except OSError as error:
        try:
            output.rmdir()
        except OSError as cleanup_error:
            raise AssemblyError(
                "assembly_marker_cleanup_failed",
                f"Could not create the incomplete marker and could not remove the empty output reservation ({cleanup_error})",
                output,
                error,
            ) from error
        raise AssemblyError("assembly_marker_create_failed", "Could not mark the reserved output as incomplete", marker, error) from error
    return marker


def _intro_skipper_component(path: Path) -> Path:
    """Require the selected official plugin release and its normal loader manifest."""
    plugin = _directory(path, "Intro Skipper plugin")
    manifest_path = _regular_file(plugin / "meta.json", "Intro Skipper manifest")
    _regular_file(plugin / "IntroSkipper.dll", "Intro Skipper assembly")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError) as error:
        raise AssemblyError("intro_skipper_manifest_invalid", "The plugin manifest could not be read", manifest_path, error) from error
    if not isinstance(manifest, dict) or str(manifest.get("guid", "")).lower() != "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b":
        raise AssemblyError("intro_skipper_identity_invalid", "The component must be the official Intro Skipper plugin", manifest_path)
    if manifest.get("version") != "12.0.4.0" or manifest.get("targetAbi") != "12.0.0.0":
        raise AssemblyError("intro_skipper_version_invalid", "The component must be Intro Skipper 12.0.4.0 for server ABI 12.0.0.0", manifest_path)
    if manifest.get("assemblies", []) not in ([], ["IntroSkipper.dll"]) or manifest.get("status", "Active") not in ("Active", 0):
        raise AssemblyError("intro_skipper_loader_invalid", "The plugin must be active and use default discovery or declare IntroSkipper.dll", manifest_path)
    return plugin


def _validated_inputs(arguments: argparse.Namespace) -> tuple[Path, dict[str, Path], Path, Path, Path, Path, bytes]:
    launcher = _regular_file(arguments.launcher_binary, "launcher executable", executable=True)
    runtimes: dict[str, Path] = {}
    for value in arguments.runtime:
        rid, separator, directory = value.partition("=")
        if not separator or not directory or re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)+", rid) is None:
            raise AssemblyError("runtime_argument_invalid", "Runtime must have the form RID=DIR with a lowercase, hyphenated RID")
        if rid in runtimes:
            raise AssemblyError("runtime_duplicate", f"Runtime {rid} was supplied more than once")
        root = _directory(Path(directory), f"{rid} runtime")
        server = _directory(root / "Server", f"{rid} server runtime")
        preparation = _directory(root / "Preparation", f"{rid} preparation runtime")
        _regular_file(server / "jellyfin", f"{rid} Jellyfin executable", executable=True)
        _regular_file(server / "svg-rasterizer" / "svg-rasterizer", f"{rid} standalone SVG converter", executable=True)
        _regular_file(preparation / "sloptank-prepare", f"{rid} preparation worker", executable=True)
        defaults = server / "defaults"
        if os.path.lexists(defaults) and (defaults.is_symlink() or not defaults.is_dir()):
            raise AssemblyError("server_defaults_invalid", f"The {rid} defaults must be a regular directory", defaults)
        for component in (server, preparation):
            if (component / ".assembly-incomplete").exists():
                raise AssemblyError("component_incomplete", f"The {rid} component has an incomplete assembly marker", component)
            _validate_tree_links(component, f"{rid} component")
        if os.path.lexists(server / "defaults" / "intro-skipper"):
            raise AssemblyError("intro_skipper_path_occupied", f"The {rid} server already contains an Intro Skipper default", server / "defaults" / "intro-skipper")
        runtimes[rid] = root
    if not {"osx-x64", "osx-arm64"}.issubset(runtimes):
        raise AssemblyError("runtime_missing", "A universal macOS app requires both osx-x64 and osx-arm64 runtimes")
    web = _directory(arguments.web_dir, "web distribution")
    ffmpeg = _directory(arguments.ffmpeg_dir, "FFmpeg distribution")
    intro_skipper = _intro_skipper_component(arguments.intro_skipper_plugin)
    materials = _directory(arguments.release_materials_dir, "release materials")
    _regular_file(web / "index.html", "web client index")
    _regular_file(ffmpeg / "ffmpeg", "ffmpeg executable", executable=True)
    _regular_file(ffmpeg / "ffprobe", "ffprobe executable", executable=True)
    for source, label in tuple((root, f"{rid} runtime") for rid, root in runtimes.items()) + ((web, "web distribution"), (ffmpeg, "FFmpeg distribution"), (intro_skipper, "Intro Skipper plugin"), (materials, "release materials")):
        if (source / ".assembly-incomplete").exists():
            raise AssemblyError("component_incomplete", f"The {label} has an incomplete assembly marker", source)
        _validate_tree_links(source, label)
    version = _require_release_value(arguments.version, "version")
    build_number = _require_release_value(arguments.build_number, "build number")
    if re.fullmatch(r"[0-9]+\.[0-9]+(?:\.[0-9]+)?", version) is None:
        raise AssemblyError("release_version_invalid", "The version must use numeric major.minor or major.minor.patch form")
    if re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", build_number) is None:
        raise AssemblyError("release_build_number_invalid", "The build number must contain only numeric components separated by dots")
    template = Path(__file__).with_name("Info.plist")
    metadata = _validate_metadata(template, version, build_number)
    return launcher, runtimes, web, ffmpeg, intro_skipper, materials, metadata


def _output_paths(requested: Path, sources: Sequence[Path]) -> tuple[Path, Path]:
    requested_output = Path(os.path.abspath(requested.expanduser()))
    if requested_output.suffix != ".app":
        raise AssemblyError("output_extension_invalid", "The output path must end in .app", requested_output)
    if os.path.lexists(requested_output):
        raise AssemblyError("output_already_exists", "The output path already exists; choose a new .app path", requested_output)
    try:
        prospective_output = requested_output.resolve(strict=False)
    except (OSError, RuntimeError) as error:
        raise AssemblyError("output_path_resolution_failed", "The prospective output path could not be resolved", requested_output, error) from error
    if os.path.lexists(prospective_output):
        raise AssemblyError("output_already_exists", "The canonical output path already exists; choose a new .app path", prospective_output)
    for source in sources:
        if _is_within(prospective_output, source) or _is_within(source, prospective_output):
            raise AssemblyError("output_overlaps_input", "The output path must be separate from every component input", prospective_output)
    try:
        prospective_output.parent.mkdir(parents=True, exist_ok=True)
        output_parent = prospective_output.parent.resolve(strict=True)
        output = output_parent / prospective_output.name
    except (OSError, RuntimeError) as error:
        raise AssemblyError("output_parent_unavailable", "The output parent directory could not be prepared", requested_output.parent, error) from error
    if os.path.lexists(output):
        raise AssemblyError("output_already_exists", "The canonical output path appeared during preparation; choose a new .app path", output)
    for source in sources:
        if _is_within(output, source) or _is_within(source, output):
            raise AssemblyError("output_overlaps_input", "The output path must be separate from every component input", output)
    return output_parent, output


def _stage_app(staging: Path, launcher: Path, runtimes: dict[str, Path], web: Path, ffmpeg: Path, intro_skipper: Path, materials: Path, metadata: bytes) -> Path:
    contents = staging / "Contents"
    resources = contents / "Resources"
    try:
        (resources / "Server").parent.mkdir(parents=True)
        (contents / "MacOS").mkdir(parents=True)
        shutil.copy2(launcher, contents / "MacOS" / "SlopTank")
        _copy_tree(intro_skipper, resources / "IntroSkipper", "Intro Skipper plugin")
        for rid, runtime in runtimes.items():
            target = resources / "Runtimes" / rid
            target.mkdir(parents=True)
            _copy_tree(runtime / "Server", target / "Server", f"{rid} server runtime")
            _copy_tree(runtime / "Preparation", target / "Preparation", f"{rid} preparation runtime")
        _copy_tree(web, resources / "Web", "web client")
        _copy_tree(ffmpeg, resources / "ffmpeg", "FFmpeg distribution")
        _copy_tree(materials, contents / "SharedSupport" / "ReleaseMaterials", "release materials")
        (contents / "Info.plist").write_bytes(metadata)
    except AssemblyError:
        raise
    except OSError as error:
        raise AssemblyError("staging_assembly_failed", "Could not assemble the application in its private staging directory", staging, error) from error
    return contents


def _publish_staged(contents: Path, output: Path) -> None:
    marker = _reserve_output(output)
    try:
        os.rename(contents, output / "Contents")
        output.chmod(0o755)
        marker.unlink()
    except OSError as error:
        raise AssemblyError("output_publish_incomplete", "Assembly could not be fully published; the output retains its incomplete marker", output, error) from error


def assemble(arguments: argparse.Namespace) -> Path:
    launcher, runtimes, web, ffmpeg, intro_skipper, materials, metadata = _validated_inputs(arguments)
    output_parent, output = _output_paths(arguments.output, (launcher, *runtimes.values(), web, ffmpeg, intro_skipper, materials))
    try:
        staging_context = tempfile.TemporaryDirectory(prefix=".sloptank-assembly-", dir=output_parent)
    except OSError as error:
        raise AssemblyError("staging_create_failed", "Could not create an isolated assembly staging directory", output_parent, error) from error
    with staging_context as temporary:
        contents = _stage_app(Path(temporary) / "SlopTank.app", launcher, runtimes, web, ffmpeg, intro_skipper, materials, metadata)
        _publish_staged(contents, output)
    return output


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Assemble a SlopTank.app from completed, prebuilt release components.")
    parser.add_argument("--launcher-binary", type=Path, required=True, help="Precompiled SlopTank Swift executable")
    parser.add_argument("--runtime", action="append", required=True, metavar="RID=DIR", help="Runtime directory containing Server and Preparation; repeat for each RID")
    parser.add_argument("--web-dir", type=Path, required=True, help="Completed web dist directory containing index.html")
    parser.add_argument("--ffmpeg-dir", type=Path, required=True, help="Complete ffmpeg/ffprobe distribution directory")
    parser.add_argument("--intro-skipper-plugin", type=Path, required=True, help="Complete official Intro Skipper 12.0.4.0 plugin directory with meta.json")
    parser.add_argument("--release-materials-dir", type=Path, required=True, help="Existing notices, source, and compliance materials")
    parser.add_argument("--version", required=True, help="Release version written to CFBundleShortVersionString")
    parser.add_argument("--build-number", required=True, help="Build number written to CFBundleVersion")
    parser.add_argument("--output", type=Path, required=True, help="New isolated SlopTank.app output path; existing paths are refused")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    try:
        result = assemble(_parser().parse_args(argv))
    except AssemblyError as error:
        print(str(error), file=sys.stderr)
        return 2
    print(f"Assembled app at {result}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
