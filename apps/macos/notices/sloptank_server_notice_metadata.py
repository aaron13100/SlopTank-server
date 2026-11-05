#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-05.
# Derived from mediaserver/tools/sloptank_server_notice_metadata.py.
"""NuGet license metadata parsing for SlopTank server notices."""

from __future__ import annotations

import json
import re
from pathlib import Path
from xml.etree import ElementTree

from sloptank_server_deployment_io import DeploymentError


BUNDLED_LICENSE_FILE_NAMES = (
    "LICENSE", "LICENSE.txt", "LICENSE.md", "License.md",
    "COPYING", "COPYING.txt", "LICENCE", "LICENCE.txt",
)


def _shipped_nuget_packages(release: Path) -> list[tuple[str, str]]:
    """Return every NuGet package actually bundled into a self-contained publish.

    Reads the publish output's own *.deps.json rather than
    Directory.Packages.props: the two disagree in both directions. A live
    check against this repository's actual publish output found 36 NuGet
    packages that ship despite never being a direct pin (transitive runtime
    dependencies like Serilog and System.Drawing.Common) and 36 direct pins
    that never ship at all (test/analyzer-only packages like Moq, xunit,
    and StyleCop.Analyzers). Only the deps.json reflects what a recipient
    of this specific published binary actually receives.
    """
    deps_files = list(release.glob("*.deps.json"))
    if len(deps_files) != 1:
        raise DeploymentError(
            f"expected exactly one *.deps.json in {release}, found {len(deps_files)}"
        )
    deps_file = deps_files[0]
    try:
        document = json.loads(deps_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise DeploymentError(f"cannot parse {deps_file}: {error}") from error
    libraries = document.get("libraries")
    if not isinstance(libraries, dict):
        raise DeploymentError(f"{deps_file} has no libraries section")

    # Runtime packs are NuGet distributions too, even though the SDK records
    # their dependency kind separately from ordinary managed packages.
    packages: list[tuple[str, str]] = []
    for key, info in libraries.items():
        if not isinstance(info, dict) or info.get("type") not in {"package", "runtimepack"}:
            continue
        name, separator, version = str(key).partition("/")
        if info["type"] == "runtimepack":
            # The SDK prefixes the dependency key, not the NuGet package ID.
            name = name.removeprefix("runtimepack.")
        if not separator or not name or not version:
            raise DeploymentError(f"{deps_file} has a malformed library key: {key!r}")
        packages.append((name, version))
    packages.sort(key=lambda item: item[0].lower())
    return packages

def _local_tag_name(element: ElementTree.Element) -> str:
    """Strip the XML namespace URI nuspec files carry on every tag."""
    return element.tag.rsplit("}", 1)[-1]

def _nuspec_license_info(nuspec_path: Path) -> dict[str, str] | None:
    """Read a nuspec's <license>/<licenseUrl> declaration, namespace-agnostic.

    NuGet has shipped several nuspec.xsd namespace versions over the years;
    matching by local tag name (not a pinned namespace URI) reads correctly
    across all of them.
    """
    try:
        root = ElementTree.parse(nuspec_path).getroot()
    except ElementTree.ParseError as error:
        raise DeploymentError(f"cannot parse {nuspec_path}: {error}") from error

    license_element = None
    license_url_element = None
    for element in root.iter():
        tag = _local_tag_name(element)
        if tag == "license" and license_element is None:
            license_element = element
        elif tag == "licenseUrl" and license_url_element is None:
            license_url_element = element

    if license_element is not None and (license_element.text or "").strip():
        return {
            "kind": license_element.get("type", "expression"),
            "value": license_element.text.strip(),
        }
    if license_url_element is not None and (license_url_element.text or "").strip():
        return {"kind": "url", "value": license_url_element.text.strip()}
    return None

def _nuspec_attribution_lines(nuspec_path: Path) -> list[str]:
    """Return declared package attribution without allowing line injection."""
    try:
        root = ElementTree.parse(nuspec_path).getroot()
    except ElementTree.ParseError as error:
        raise DeploymentError(f"cannot parse {nuspec_path}: {error}") from error

    values: dict[str, str] = {}
    for element in root.iter():
        tag = _local_tag_name(element)
        if tag not in {"authors", "owners", "copyright"} or tag in values:
            continue
        value = " ".join((element.text or "").split())
        if value:
            values[tag] = value

    lines: list[str] = []
    if "copyright" in values:
        lines.append(f"Copyright: {values['copyright']}")
    if "authors" in values:
        lines.append(f"Package authors: {values['authors']}")
    if "owners" in values and values["owners"] != values.get("authors"):
        lines.append(f"Package owners: {values['owners']}")
    return lines

def _extract_license_ids(expression: str) -> list[str]:
    """Split a simple SPDX AND/OR expression into its distinct license ids.

    Mirrors jellyfin-web-codex/scripts/webpack/third-party-notices-plugin.js
    extractLicenseIds: every license expression seen in this dependency
    tree is a simple `ID`, `(ID)`, or `ID AND ID` shape, so splitting on the
    boolean keywords is sufficient without a full SPDX grammar parser.
    """
    if not expression:
        return []
    tokens = re.split(r"\s+(?:AND|OR|WITH)\s+", expression.replace("(", " ").replace(")", " "), flags=re.IGNORECASE)
    ids: list[str] = []
    seen: set[str] = set()
    for token in tokens:
        token = token.strip()
        if token and token not in seen:
            seen.add(token)
            ids.append(token)
    return ids

def _read_corpus_license_text(license_texts_dir: Path, license_id: str) -> str | None:
    candidate = license_texts_dir / f"{license_id}.txt"
    if not candidate.is_file():
        return None
    return candidate.read_text(encoding="utf-8").strip()

def _load_license_overrides(path: Path) -> dict[str, dict[str, str]]:
    """Load exact-version NuGet license resolutions from the audited SBOM input."""
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise DeploymentError(f"cannot parse NuGet license overrides {path}: {error}") from error
    if not isinstance(document, dict):
        raise DeploymentError(f"NuGet license overrides must be a JSON object: {path}")

    overrides: dict[str, dict[str, str]] = {}
    for purl, value in document.items():
        if not isinstance(purl, str) or not purl.casefold().startswith("pkg:nuget/"):
            raise DeploymentError(f"invalid NuGet override package URL: {purl!r}")
        if not isinstance(value, dict):
            raise DeploymentError(f"NuGet license override must be an object: {purl}")
        license_id = value.get("license")
        source = value.get("source")
        attribution = value.get("attribution")
        if not isinstance(license_id, str) or not license_id.strip():
            raise DeploymentError(f"NuGet license override has no license: {purl}")
        if not isinstance(source, str) or not source.startswith("https://"):
            raise DeploymentError(f"NuGet license override has no HTTPS evidence source: {purl}")
        if attribution is not None and (
            not isinstance(attribution, str) or not attribution.strip()
        ):
            raise DeploymentError(f"NuGet license override has invalid attribution: {purl}")
        if attribution is not None and re.search(
            r"\[(?:year|fullname|name|copyright holder)\]", attribution,
            flags=re.IGNORECASE,
        ):
            raise DeploymentError(
                f"NuGet license override has unresolved attribution placeholder: {purl}"
            )
        overrides[purl.casefold()] = {
            "license": license_id.strip(),
            "source": source,
            **({"attribution": attribution.strip()} if attribution is not None else {}),
        }
    return overrides

def _find_bundled_license_file(package_dir: Path) -> tuple[str, str] | None:
    for file_name in BUNDLED_LICENSE_FILE_NAMES:
        candidate = package_dir / file_name
        if candidate.is_file():
            return file_name, candidate.read_text(encoding="utf-8", errors="replace").strip()
    return None

def _spdx_license_text_lines(
    expression: str,
    license_texts_dir: Path,
) -> tuple[list[str], list[str]]:
    """Resolve an SPDX expression to ordered canonical-text notice lines."""
    lines: list[str] = []
    missing_ids: list[str] = []
    for license_id in _extract_license_ids(expression):
        corpus_text = _read_corpus_license_text(license_texts_dir, license_id)
        if corpus_text is None:
            missing_ids.append(license_id)
            continue
        lines.extend([
            "",
            f'--- Full text of referenced license "{license_id}" (SPDX canonical text) ---',
            "",
            corpus_text,
        ])
    return lines, missing_ids
