# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Execute one verified additive audio companion rewrite.

The catch-up CLI and the organizer's prepublication gate share this worker so
both paths use the same planner, output verification, playback admission, and
permalink-safe replacement.
"""

from __future__ import annotations

import time
from pathlib import Path

from lib.audio_defaults import _probe_audio_streams
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.media_write_lock import MediaWriteBusy, exclusive_media_write
from lib.media_probe import _probe_video_audio
from lib.playback_start_gate import sessions_are_idle as _sessions_are_idle
from lib.transcode import _added_audio_from_streams, run_encode
from lib.tv_audio_catchup_install import install_verified_candidate
from lib.tv_audio_catchup_plan import (
    VerifyError,
    build_add_track_cmd,
    plan_catchup_tracks,
    require_publication_suffix,
    source_path_error,
    verify_output,
)


def run_add_track_encode(cmd: list[str], tmp: Path, *, source_size: int,
                         encode=run_encode):
    """Run an additive stream-copy remux under progress and size-derived bounds."""
    return encode(
        cmd,
        timeout=stream_copy_outer_timeout_seconds(source_size),
        progress_path=tmp,
        extend_while_progressing=True,
    )


def _rewrite(path: Path, tmp: Path, cmd: list[str], streams, tracks, *,
             sessions_are_idle, probe_audio, run_encode_with_watchdog,
             install_candidate, verify=verify_output,
             container_suffix: str | None = None) -> dict:
    """Encode, prove all source audio plus companions, then safely install.

    ``container_suffix`` is the publication container the companions were
    planned for; omitted, it is resolved from ``path``.
    """
    t0 = time.monotonic()
    try:
        result = run_encode_with_watchdog(
            cmd, tmp, source_size=path.stat().st_size)
    except Exception as exc:
        tmp.unlink(missing_ok=True)
        return {"path": str(path), "status": "ffmpeg_error", "error": str(exc)}
    if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size == 0:
        tmp.unlink(missing_ok=True)
        return {"path": str(path), "status": "ffmpeg_failed",
                "returncode": result.returncode, "stderr": result.stderr[-2000:]}
    after = probe_audio(tmp)
    try:
        verify(streams, after, tracks)
        leftover = _added_audio_from_streams(
            after, container_suffix or require_publication_suffix(path))
        if leftover:
            raise VerifyError(
                f"output still needs {len(leftover)} added track(s): {leftover}")
    except VerifyError as exc:
        tmp.unlink(missing_ok=True)
        return {"path": str(path), "status": "verify_failed", "error": str(exc)}
    installed = install_candidate(tmp, path,
                                  sessions_are_idle=sessions_are_idle)
    if installed["status"] == "fixed":
        installed["seconds"] = round(time.monotonic() - t0, 1)
    return installed


def process_one(path: Path, *, sessions_are_idle=_sessions_are_idle,
                probe_audio=_probe_audio_streams,
                write_lock=exclusive_media_write,
                run_encode_with_watchdog=run_add_track_encode,
                install_candidate=install_verified_candidate,
                plan_tracks=plan_catchup_tracks,
                make_command=build_add_track_cmd,
                validate_source=source_path_error,
                probe_video=_probe_video_audio,
                verify=verify_output) -> dict:
    """Add verified browser/TV audio companions to one path, returning status."""
    path_error = validate_source(path)
    if path_error is not None:
        return {"path": str(path), "status": "invalid_source_path", "error": path_error}
    if not path.exists():
        return {"path": str(path), "status": "missing"}
    streams = probe_audio(path)
    if not streams:
        video_facts = probe_video(path)
        if video_facts and video_facts.get("audio_codec") is None:
            return {"path": str(path), "status": "no_audio_tracks"}
        return {"path": str(path), "status": "unprobeable"}
    try:
        container = require_publication_suffix(path, probe_video=probe_video)
    except VerifyError as exc:
        return {"path": str(path), "status": "unprobeable", "error": str(exc)}
    try:
        tracks = plan_tracks(path, streams, container_suffix=container)
    except VerifyError as exc:
        return {"path": str(path), "status": "invalid_audio_metadata",
                "error": str(exc)}
    if not tracks:
        return {"path": str(path), "status": "already_compliant"}
    try:
        with write_lock(path, "tvaudio-add") as tmp:
            cmd = make_command(path, tmp, streams, tracks)
            try:
                return _rewrite(
                    path, tmp, cmd, streams, tracks,
                    sessions_are_idle=sessions_are_idle,
                    probe_audio=probe_audio,
                    run_encode_with_watchdog=run_encode_with_watchdog,
                    install_candidate=install_candidate,
                    verify=verify,
                    container_suffix=container,
                )
            except BaseException:
                tmp.unlink(missing_ok=True)
                raise
    except MediaWriteBusy as exc:
        return {"path": str(path), "status": "busy", "error": str(exc)}


class AudioPreparationError(RuntimeError):
    """The audio-only compatibility remux did not reach a verified ready state."""

    def __init__(self, path: Path, result: dict):
        self.code = "audio_compatibility_preparation_failed"
        self.path = path
        self.result = result
        detail = result.get("error") or result.get("status") or "unknown worker result"
        super().__init__(f"{self.code}: {path}: {detail}")


def _require_audio_ready(path: Path, result: dict) -> dict:
    """Return a terminal ready result or raise the shared preparation error."""
    if result.get("status") not in {
            "fixed", "already_compliant", "already_tv_playable",
            "no_audio_tracks"}:
        raise AudioPreparationError(path, result)
    return result


def prepare_audio_before_publication(path: Path, *,
                                     process_path=process_one) -> dict:
    """Return only after shared planning proves this source is audio-ready.

    Any nonterminal preparation result raises, leaving the caller's source
    outside the library and available for a later organizer retry.
    """
    # This path is not visible to Jellyfin yet, so replacing it cannot race a
    # reader. run_encode still applies the adaptive playback clamp while the
    # candidate is produced; only the live-library install gate is inapplicable.
    result = process_path(path, sessions_are_idle=lambda: True)
    return _require_audio_ready(path, result)


def prepare_audio_before_video_conversion(
        path: Path, *, sessions_are_idle=_sessions_are_idle,
        process_path=process_one) -> dict:
    """Prepare audio on an existing library file before video conversion."""
    result = process_path(path, sessions_are_idle=sessions_are_idle)
    return _require_audio_ready(path, result)
