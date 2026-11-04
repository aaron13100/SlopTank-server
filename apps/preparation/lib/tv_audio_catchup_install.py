# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Fail-closed installation boundary for a verified TV-audio candidate."""

from __future__ import annotations

from pathlib import Path
from typing import Callable

from lib.media_identity import replace_media_file


def _discard(candidate: Path) -> str | None:
    try:
        candidate.unlink(missing_ok=True)
    except OSError as exc:
        return str(exc)[:500]
    return None


def install_verified_candidate(
    candidate: Path,
    original: Path,
    *,
    sessions_are_idle: Callable[[], bool],
    replace: Callable[[Path, Path], Path] = replace_media_file,
) -> dict:
    """Replace only after a fresh idle observation; otherwise discard candidate."""
    try:
        idle = sessions_are_idle()
    except Exception as exc:  # noqa: BLE001 - observer uncertainty must fail closed
        cleanup_error = _discard(candidate)
        result = {
            "path": str(original),
            "status": "playback_state_unknown",
            "error": str(exc)[:500],
        }
        if cleanup_error is not None:
            result["cleanup_error"] = cleanup_error
        return result
    if idle is not True:
        cleanup_error = _discard(candidate)
        result = {"path": str(original), "status": "playback_started"}
        if cleanup_error is not None:
            result["cleanup_error"] = cleanup_error
        return result
    try:
        replace(candidate, original)
    except Exception as exc:  # noqa: BLE001 - one failure must not end the batch
        cleanup_error = _discard(candidate)
        result = {
            "path": str(original),
            "status": "replace_failed",
            "error": str(exc)[:500],
        }
        if cleanup_error is not None:
            result["cleanup_error"] = cleanup_error
        return result
    return {"path": str(original), "status": "fixed"}
