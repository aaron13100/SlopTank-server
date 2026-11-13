# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Assess macOS distribution signatures, stapled tickets and Gatekeeper policy."""

from __future__ import annotations

from pathlib import Path

from smoke_deadline import Deadline, SmokeError, run


DEVELOPER_TEAM_ID = "6227ZC5AC2"


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
