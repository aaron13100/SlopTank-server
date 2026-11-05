#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-05.
# Derived from mediaserver/tools/sloptank_server_notices.py.
"""Fail-closed third-party notice construction and staging."""

from __future__ import annotations

import shutil
from pathlib import Path

from sloptank_server_deployment_io import DeploymentError
from sloptank_server_notice_metadata import (
    _load_license_overrides,
    _nuspec_attribution_lines,
    _nuspec_license_info,
    _read_corpus_license_text,
    _shipped_nuget_packages,
    _spdx_license_text_lines,
)


FFMPEG_SOURCE_OFFER = "FFMPEG-SOURCE-OFFER.md"
FFMPEG_LICENSE = "FFMPEG-GPL-3.0.txt"
# Markers that identify a notices file as this generator's output rather than a
# stale or hand-written one. sloptank_server_qualification's notice gate imports
# them instead of repeating the literals: when it held its own copies the two
# drifted on a single capital letter and refused every real release, while the
# gate's fixtures, which copied the gate's literal, kept passing.
NOTICE_TITLE = "SlopTank server - Third-Party Notices"
NOTICE_BUNDLE_MARKER = "NuGet packages are bundled into this release."


def _audited_notice_lines(
    name: str,
    version: str,
    override: dict[str, str],
    license_texts_dir: Path,
) -> list[str]:
    """Build notice lines from an audited exact-version override."""
    expression = override["license"]
    lines = [
        f"{name} {version}",
        f"Declared license: {expression}",
        f"Audited evidence: {override['source']}",
    ]
    attribution = override.get("attribution")
    if attribution:
        lines.extend(["", "--- Preserved attribution and notices ---", "", attribution])
    license_lines, missing_ids = _spdx_license_text_lines(
        expression, license_texts_dir)
    lines.extend(license_lines)
    if missing_ids:
        raise DeploymentError(
            f"{name} {version} has audited license {expression!r} but no canonical "
            f"text is available locally for: {', '.join(missing_ids)}. "
            f"Add it under {license_texts_dir}."
        )
    return lines

def _declared_notice_lines(
    name: str,
    version: str,
    package_dir: Path,
    info: dict[str, str] | None,
    license_texts_dir: Path,
) -> list[str]:
    """Build notice lines from local package-declared license metadata."""
    if info is None:
        raise DeploymentError(
            f"{name} {version} has no local license metadata and no audited exact-version override"
        )
    if info["kind"] == "url":
        raise DeploymentError(
            f"{name} {version} declares only licenseUrl {info['value']!r} and has no "
            "audited exact-version override"
        )

    lines = [f"{name} {version}"]
    if info["kind"] == "file":
        bundled_path = package_dir / info["value"]
        if not bundled_path.is_file():
            raise DeploymentError(
                f"{name} {version} nuspec declares license file {info['value']!r} "
                f"but it does not exist at {bundled_path}"
            )
        lines.extend([
            f"Declared license: file {info['value']}",
            "",
            f"--- License text as bundled by the package ({info['value']}) ---",
            "",
            bundled_path.read_text(encoding="utf-8", errors="replace").strip(),
        ])
        return lines

    expression = info["value"]
    lines.append(f"Declared license: {expression}")
    license_lines, missing_ids = _spdx_license_text_lines(
        expression, license_texts_dir)
    lines.extend(license_lines)
    if missing_ids:
        raise DeploymentError(
            f"{name} {version} declares SPDX license expression {expression!r} but no "
            f"canonical text is available locally for: {', '.join(missing_ids)}. "
            f"Add it under {license_texts_dir}."
        )
    return lines

def _third_party_notice_entry(
    name: str,
    version: str,
    nuget_cache: Path,
    license_texts_dir: Path,
    license_overrides: dict[str, dict[str, str]],
) -> str:
    """Build one THIRD-PARTY-NOTICES.txt entry for a single shipped package.

    The audited exact-version override wins when present. Otherwise the
    package must provide a bundled license file or an SPDX expression whose
    complete canonical text exists locally. Missing evidence always fails
    closed; a release can never carry UNKNOWN or UNRESOLVED placeholders.
    """
    package_dir = nuget_cache / name.lower() / version
    nuspec_path = package_dir / f"{name.lower()}.nuspec"
    info = _nuspec_license_info(nuspec_path) if nuspec_path.is_file() else None

    purl = f"pkg:nuget/{name}@{version}".casefold()
    override = license_overrides.get(purl)
    if override is not None:
        lines = _audited_notice_lines(
            name, version, override, license_texts_dir)
        insertion_index = 3
    else:
        lines = _declared_notice_lines(
            name, version, package_dir, info, license_texts_dir)
        insertion_index = 2
    if nuspec_path.is_file():
        lines[insertion_index:insertion_index] = _nuspec_attribution_lines(nuspec_path)
    if package_dir.is_dir():
        # The primary grant does not replace notices for native/transitive code
        # bundled by the package (for example .NET and native Skia assets).
        for notice_path in sorted(package_dir.iterdir()):
            normalized_name = notice_path.stem.casefold().replace("-", "").replace("_", "")
            if (normalized_name != "thirdpartynotices"
                    or notice_path.suffix.casefold() not in {"", ".txt", ".md"}
                    or not notice_path.is_file()):
                continue
            try:
                supplied_notices = notice_path.read_bytes().decode("utf-8")
            except (OSError, UnicodeError) as error:
                raise DeploymentError(
                    f"{name} {version} cannot read bundled notices {notice_path}: {error}"
                ) from error
            lines.extend([
                "", f"--- Preserved package notices ({notice_path.name}) ---", "",
                supplied_notices,
            ])
    return "\n".join(lines)

def _third_party_notices_text(
    packages: list[tuple[str, str]],
    nuget_cache: Path,
    license_texts_dir: Path,
    license_overrides: dict[str, dict[str, str]],
    *,
    title: str = NOTICE_TITLE,
) -> str:
    entries = [
        _third_party_notice_entry(
            name, version, nuget_cache, license_texts_dir, license_overrides
        )
        for name, version in packages
    ]
    divider = "=" * 72
    header = (
        f"{title}\n\n"
        "This distribution bundles the NuGet packages listed below, each under\n"
        "its own license. Every entry lists the package name, version, its\n"
        "declared or audited license, applicable license text, and preserved\n"
        "attribution where required. The build fails rather than publishing an\n"
        "unknown or unresolved license. Entries are ordered alphabetically by\n"
        "package name.\n\n"
        f"Derived from the self-contained publish's own dependency manifest "
        f"(*.deps.json): {len(entries)} {NOTICE_BUNDLE_MARKER}\n\n"
        f"{divider}"
    )
    body = f"\n\n{divider}\n\n".join(entries)
    return f"{header}\n\n{body}\n"

def _stage_license_and_notices(
    staging: Path,
    repository: Path,
    nuget_cache: Path,
    license_texts_dir: Path,
    license_overrides_path: Path,
) -> None:
    """Copy the project LICENSE and write an aggregate NuGet third-party
    notices file into a freshly published release, so every release and
    the app bundle built from it both carry them.

    GPLv2 Sections 1 and 3 require the license text and a source offer to
    accompany a distributed binary; see
    deliverables/gpl-license-boundary-audit-t_260730_175528_423.md Part 2.1.
    """
    license_source = repository / "LICENSE"
    if not license_source.is_file():
        raise DeploymentError(f"server LICENSE does not exist: {license_source}")
    shutil.copyfile(license_source, staging / "LICENSE")

    source_offer = repository / FFMPEG_SOURCE_OFFER
    if not source_offer.is_file():
        raise DeploymentError(f"FFmpeg source offer does not exist: {source_offer}")
    shutil.copyfile(source_offer, staging / FFMPEG_SOURCE_OFFER)

    lgpl_and_gpl = _read_corpus_license_text(license_texts_dir, "LGPL-3.0-only")
    gpl_heading = "GNU GENERAL PUBLIC LICENSE\n"
    if lgpl_and_gpl is None or gpl_heading not in lgpl_and_gpl:
        raise DeploymentError(
            "canonical LGPL-3.0-only text does not contain the incorporated GPLv3 text"
        )
    gpl_text = gpl_heading + lgpl_and_gpl.split(gpl_heading, 1)[1]
    (staging / FFMPEG_LICENSE).write_text(gpl_text, encoding="utf-8")

    packages = _shipped_nuget_packages(staging)
    license_overrides = _load_license_overrides(license_overrides_path)
    notices = _third_party_notices_text(
        packages, nuget_cache, license_texts_dir, license_overrides
    )
    (staging / "THIRD-PARTY-NOTICES.txt").write_text(notices, encoding="utf-8")

    converter = staging / "svg-rasterizer"
    if converter.exists():
        if not (converter / "LICENSE").is_file():
            raise DeploymentError(f"standalone SVG converter LICENSE does not exist: {converter}")
        converter_notices = _third_party_notices_text(
            _shipped_nuget_packages(converter), nuget_cache, license_texts_dir,
            license_overrides, title="Standalone SVG converter - Third-Party Notices",
        )
        (converter / "THIRD-PARTY-NOTICES.txt").write_text(converter_notices, encoding="utf-8")
