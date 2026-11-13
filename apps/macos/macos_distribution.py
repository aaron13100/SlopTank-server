# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Assess macOS distribution signatures, stapled tickets and Gatekeeper policy."""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any

from smoke_deadline import Deadline, SmokeError, run


DEVELOPER_TEAM_ID = "6227ZC5AC2"
WEB_COMPONENT = "web:@jellyfin/libass-wasm@4.2.3"
WEB_KIT_ENTRIES = (
    "LGPL-COMPLIANCE.json",
    "LGPL-CUSTOMER-TERMS.txt",
    "PROPRIETARY-DEPENDENCIES.json",
    "LGPL-RELINKING.md",
    "LGPL-SOURCES",
)
WEB_VENDOR_ARTIFACTS = (
    "libraries/default.woff2",
    "libraries/subtitles-octopus-worker.wasm",
    "libraries/subtitles-octopus-worker.js",
    "libraries/subtitles-octopus-worker-legacy.js",
)
WEB_TRANSFORMED_ARTIFACTS = (
    "libraries/subtitles-octopus-worker.js",
    "libraries/subtitles-octopus-worker-legacy.js",
)
WEB_PROOF_ROOT = Path("LGPL-SOURCES/web-build-evidence")
WEB_PROBE_SUITES = (
    "modified worker URL load",
    "modified WASM ABI load",
    "ASS subtitle rendering",
    "legacy worker fallback",
    "subtitle playback E2E",
)
WEB_WRAPPER_PATTERN = re.compile(
    r"node_modules\.@jellyfin\.libass-wasm\.[0-9a-f]{20}\.chunk\.js"
)


def check_web_lgpl_kit(app: Path) -> str:
    """Require the packed web kit to describe and bind this app's current bytes."""
    web = app / "Contents" / "Resources" / "Web"

    def regular_file(root: Path, relative: str) -> Path:
        path = root / relative
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"missing or symlinked LGPL web file: {relative}")
        return path

    def digest(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    if web.is_symlink() or not web.is_dir():
        raise ValueError("app has no regular Resources/Web directory")
    for name in WEB_KIT_ENTRIES:
        path = web / name
        if path.is_symlink() or not (path.is_dir() if name == "LGPL-SOURCES" else path.is_file()):
            raise ValueError(f"web LGPL kit is incomplete or symlinked: {name}")

    wrappers = list(web.glob("node_modules.@jellyfin.libass-wasm.*.chunk.js"))
    if (len(wrappers) != 1 or wrappers[0].is_symlink() or not wrappers[0].is_file()
            or not WEB_WRAPPER_PATTERN.fullmatch(wrappers[0].name)):
        raise ValueError("web LGPL kit requires exactly one well-formed libass wrapper")
    actual = {name: digest(regular_file(web, name)) for name in WEB_VENDOR_ARTIFACTS}
    wrapper = wrappers[0]
    actual[wrapper.name] = digest(wrapper)
    transformed = {name: actual[name] for name in WEB_TRANSFORMED_ARTIFACTS}
    transformed[wrapper.name] = actual[wrapper.name]

    try:
        manifest = json.loads(regular_file(web, "LGPL-COMPLIANCE.json").read_text(encoding="utf-8"))
        if not isinstance(manifest, dict):
            raise ValueError("manifest root is not an object")
        entry = manifest["components"][WEB_COMPONENT]
        replacement = entry["replacement_probe"]
        reproduction = entry["reproduction"]
    except (OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError(f"web LGPL compliance manifest is malformed: {error}") from error
    if (not isinstance(entry, dict) or not isinstance(replacement, dict)
            or not isinstance(reproduction, dict)):
        raise ValueError("web LGPL component evidence is malformed")
    if manifest.get("schema") != "org.sloptank.lgpl-compliance/v1" or manifest.get("review_status") != "pass":
        raise ValueError("web LGPL compliance manifest is not approved")
    if replacement.get("status") != "pass" or replacement.get("suites") != list(WEB_PROBE_SUITES):
        raise ValueError("web LGPL replacement probe inventory is not passing")
    if replacement.get("artifact_sha256") != actual:
        raise ValueError("web LGPL replacement probe hashes do not match packaged runtime files")
    if reproduction.get("status") != "pass" or reproduction.get("artifacts") != transformed:
        raise ValueError("web LGPL reproduction hashes do not match packaged transformed files")

    proof_root = web / WEB_PROOF_ROOT
    if proof_root.is_symlink() or not proof_root.is_dir():
        raise ValueError("web LGPL artifact-local proof directory is missing")

    def receipt(section: dict[str, Any], filename: str) -> dict[str, Any]:
        evidence = section.get("evidence")
        if not isinstance(evidence, dict):
            raise ValueError(f"web LGPL proof metadata is missing: {filename}")
        relative = evidence.get("receipt")
        path = Path(relative) if isinstance(relative, str) else Path("..")
        if path.is_absolute() or ".." in path.parts or not path.as_posix().startswith(WEB_PROOF_ROOT.as_posix() + "/"):
            raise ValueError(f"web LGPL receipt escapes artifact-local proof directory: {filename}")
        if path.as_posix() != (WEB_PROOF_ROOT / filename).as_posix():
            raise ValueError(f"web LGPL receipt path is not the expected local record: {filename}")
        receipt_path = regular_file(web, path.as_posix())
        if not receipt_path.resolve().is_relative_to(proof_root.resolve()):
            raise ValueError(f"web LGPL receipt resolves outside proof directory: {filename}")
        if digest(receipt_path) != evidence.get("sha256"):
            raise ValueError(f"web LGPL receipt digest mismatch: {filename}")
        try:
            document = json.loads(receipt_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise ValueError(f"web LGPL receipt is unreadable: {filename}: {error}") from error
        if not isinstance(document, dict) or document.get("status") != "PASS":
            raise ValueError(f"web LGPL receipt is not PASS: {filename}")
        return document

    probe_receipt = receipt(replacement, "replacement-probes.json")
    runs = probe_receipt.get("runs")
    if (probe_receipt.get("failures") != [] or probe_receipt.get("artifact_sha256") != actual
            or not isinstance(runs, list) or not all(isinstance(run, dict) for run in runs)
            or any(not isinstance(run.get("suites"), list)
                   or not all(isinstance(suite, str) for suite in run["suites"])
                   for run in runs)
            or sorted(suite for run in runs for suite in run["suites"]) != sorted(WEB_PROBE_SUITES)
            or any(run.get("playwright_exit") != 0 or run.get("failures") != [] for run in runs)):
        raise ValueError("web LGPL replacement receipt does not bind passing current-byte suites")
    reproduction_receipt = receipt(reproduction, "reproduction.json")
    entries = reproduction_receipt.get("entries")
    if (not isinstance(entries, dict) or set(entries) != set(transformed)
            or not all(isinstance(entries[name], dict) for name in transformed) or any(
        entries[name].get("matches") is not True
        or entries[name].get("reproduced_sha256") != transformed[name]
        or entries[name].get("shipped_sha256") != transformed[name]
        for name in transformed
    )):
        raise ValueError("web LGPL reproduction receipt does not bind current transformed bytes")
    return "web LGPL kit and current libass runtime map verified"


def distribution_checks(dmg: Path, app: Path, deadline: Deadline, *, notarized: bool) -> dict[str, str]:
    """Assess every independent control before allowing application execution."""
    commands = {
        "app_seal": ["/usr/bin/codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app)],
        "app_signature": ["/usr/bin/codesign", "--display", "--verbose=4", str(app)],
    }
    if notarized:
        commands.update({
            "stapled_ticket": ["/usr/bin/xcrun", "stapler", "validate", str(dmg)],
            "dmg_assessment": ["/usr/sbin/spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose=4", str(dmg)],
            "app_assessment": ["/usr/sbin/spctl", "--assess", "--type", "execute", "--verbose=4", str(app)],
        })
    results = {}
    failures = {}
    if notarized:
        try:
            results["lgpl_web_kit"] = check_web_lgpl_kit(app)
        except (OSError, ValueError) as error:
            results["lgpl_web_kit"] = f"{type(error).__name__}: {error}"
            failures["lgpl_web_kit"] = results["lgpl_web_kit"]
    for name, command in commands.items():
        try:
            result = run(command, name, deadline, timeout=120, check=False)
        except (SmokeError, OSError) as error:
            results[name] = f"{type(error).__name__}: {error}"
            failures[name] = results[name]
            continue
        results[name] = result.stdout + result.stderr
        if result.returncode:
            failures[name] = f"exit {result.returncode}: {results[name]}"
    signature = results["app_signature"]
    if (f"TeamIdentifier={DEVELOPER_TEAM_ID}" not in signature or "Timestamp=" not in signature
            or "(runtime)" not in signature or "Authority=Developer ID Application:" not in signature):
        failures["identity"] = f"App lacks the expected Developer ID identity, timestamp or runtime: {signature}"
    if notarized and "source=Notarized Developer ID" not in results["app_assessment"]:
        failures["notarization"] = results["app_assessment"]
    if failures:
        raise SmokeError(f"Distribution checks failed: {failures!r}")
    return results
