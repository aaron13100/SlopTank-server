#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Cheap, copy-only container fixes: MKV duration-header repair, faststart, legacy remux.

None of these re-encode video/audio -- they only rewrite the container so
Jellyfin's HLS segmenter and browsers can handle the file without a
server-side transcode.
"""

from __future__ import annotations

import glob as globlib
import json
import logging
from lib.media_identity import replace_media_file
from pathlib import Path

from lib.organize_media_config import REMUX_FOR_STREAMING, _REMUX_FROM_EXTS
from lib.media_probe import _ffmpeg_stderr_tail
from lib.media_write_lock import MediaWriteBusy, exclusive_media_write
from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.encode_priority import run_encode
from lib.media_tools import require_media_tools, require_tool
from lib.remux_input_policy import remux_input_args

log = logging.getLogger("organize-media")


def _mp4_has_faststart(path: Path) -> bool:
    """Return True if an MP4's moov atom precedes mdat (i.e. streamable)."""
    try:
        with open(path, "rb") as fh:
            head = fh.read(1024 * 1024)
    except Exception:
        return True  # err on side of leaving the file alone
    moov = head.find(b"moov")
    mdat = head.find(b"mdat")
    if moov == -1:
        return False
    if mdat == -1:
        return True
    return moov < mdat


def _move_sidecars_to_new_stem(old_path: Path, new_path: Path):
    """When remux changes the file extension, retarget any sidecars sharing the stem."""
    if old_path.stem == new_path.stem and old_path.parent == new_path.parent:
        return
    for sidecar in old_path.parent.glob(globlib.escape(old_path.stem) + ".*"):
        if sidecar == old_path or sidecar == new_path:
            continue
        target = new_path.parent / (new_path.stem + sidecar.name[len(old_path.stem):])
        if target.exists():
            continue
        try:
            # allow-bare-rename: _move_sidecars_to_new_stem retargets a
            # subtitle/artwork sidecar's stem after a container remux; the
            # video swap in this same file already goes through
            # replace_media_file (see the three calls this test's writer check
            # verifies)
            sidecar.rename(target)
        except Exception:
            # allow-silent-catch: best-effort sidecar retarget; a sidecar that
            # can't be renamed just stays under its old stem, orphaned but intact.
            continue


def _mkv_duration_mismatch(path: Path) -> tuple[float, float] | None:
    """Return (declared, actual) duration in seconds if they disagree by >=30s, else None."""
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cancellable_capture(
            [ffprobe, "-v", "error", "-show_entries", "format=duration",
             "-of", "json", str(path)],
            # See media_probe._probe_video_audio's timeout comment: a cold
            # read of a large library file can itself take up to a minute or
            # more on this host, well past a 30s ceiling.
            timeout=300,
        )
        if result.returncode != 0 or not result.stdout:
            return None
        declared = float(json.loads(result.stdout).get("format", {}).get("duration") or 0)
    except EncodeCancelled:
        raise
    except Exception:
        # allow-silent-catch: ffprobe failure means the header can't be
        # checked; caller already treats None as "nothing to fix".
        return None
    if declared <= 0:
        return None
    # Scan the actual last video packet timestamp (fast: seeks to near-end).
    try:
        result = run_cancellable_capture(
            [ffprobe, "-v", "error", "-select_streams", "v:0",
             "-show_entries", "packet=pts_time", "-of", "csv",
             "-read_intervals", f"%{max(0, declared - 120)}%+#9999",
             str(path)],
            timeout=60,
        )
        pts_lines = [l for l in result.stdout.splitlines() if l.startswith("packet,")]
        if not pts_lines:
            return None
        actual = float(pts_lines[-1].split(",")[1])
    except EncodeCancelled:
        raise
    except Exception:
        # allow-silent-catch: ffprobe failure means the actual end can't be
        # measured; caller already treats None as "nothing to fix".
        return None
    if abs(declared - actual) < 30:
        return None  # header is close enough -- no action needed
    return declared, actual


def _remux_mkv_in_place(
        path: Path, declared: float, actual: float,
        ):
    """Regenerate a clean MKV container (correct duration, indexed Cues) via copy-only remux."""
    log.info("mkv header duration mismatch on %s (declared=%.1fs actual=%.1fs) -- remuxing",
             path.name, declared, actual)
    # Above the try: the handler below swallows Exception for best-effort
    # repair, which must not swallow a missing tool.
    ffmpeg = require_tool("ffmpeg")
    try:
        with exclusive_media_write(path, "remuxing", suffix=".mkv") as tmp:
            try:
                result = run_encode(
                    [ffmpeg, "-nostdin", "-y", "-i", str(path),
                     "-c", "copy", "-map", "0", "-map_metadata", "0", str(tmp)],
                    timeout=28800, progress_path=tmp,
                )
                if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size < path.stat().st_size * 0.95:
                    log.warning("mkv remux failed for %s: rc=%s; ffmpeg said: %s",
                                path.name, result.returncode, _ffmpeg_stderr_tail(result))
                    tmp.unlink(missing_ok=True)
                    return
                replace_media_file(tmp, path)
                log.info("mkv remux complete: %s", path.name)
            except Exception:
                # allow-silent-catch: best-effort repair; on any ffmpeg/IO failure the
                # original file is left untouched (tmp is cleaned up, never swapped in).
                tmp.unlink(missing_ok=True)
    except MediaWriteBusy as exc:
        log.warning("mkv remux skipped for %s: %s", path.name, exc)


def _fix_mkv_container(
        path: Path, ):
    """Remux an MKV whose declared duration is wrong (>30s off from actual content).

    Some muxers (e.g. certain streaming rips) write an inflated duration in the
    Matroska Segment Info block while the actual packets end much earlier.  This
    causes Jellyfin's NEbml keyframe extractor to seek past the real EOF and fail
    with EndOfStreamException, falling back to time-based HLS segmentation which
    produces freezing at every segment boundary.

    A copy-only remux via ffmpeg regenerates a clean container with a correct
    duration and a properly-indexed Cues element.
    """
    if path.suffix.lower() != ".mkv":
        return
    require_media_tools()
    mismatch = _mkv_duration_mismatch(path)
    if mismatch is None:
        return
    _remux_mkv_in_place(path, *mismatch)


def _remux_legacy_container(
        path: Path, ffmpeg: str, ) -> Path:
    """Remux a legacy container (.avi/.wmv/.mpg/.mpeg/.mov) into MKV, copy-only."""
    new_path = path.with_suffix(".mkv")
    if new_path.exists():
        return path
    log.info("remux for streaming: %s -> %s", path.name, new_path.name)
    try:
        with exclusive_media_write(path, "remuxstream", suffix=".mkv") as tmp:
            result = run_encode(
                _legacy_remux_command(
                    path, tmp, ffmpeg, include_all_streams=True),
                timeout=28800, progress_path=tmp,
            )
            if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size < path.stat().st_size * 0.8:
                # Some legacy containers carry streams MKV cannot copy; only then
                # fall back to video/audio rather than discarding subtitles eagerly.
                tmp.unlink(missing_ok=True)
                result = run_encode(
                    _legacy_remux_command(
                        path, tmp, ffmpeg, include_all_streams=False),
                    timeout=28800, progress_path=tmp,
                )
            if result.returncode != 0 or not tmp.exists():
                log.warning("legacy remux failed for %s: rc=%s; ffmpeg said: %s",
                            path.name, result.returncode, _ffmpeg_stderr_tail(result))
                tmp.unlink(missing_ok=True)
                return path
            replace_media_file(tmp, path, new_path)
            _move_sidecars_to_new_stem(path, new_path)
            return new_path
    except MediaWriteBusy as exc:
        log.info("legacy remux skipped for %s: %s", path.name, exc)
        return path
    except Exception:
        # allow-silent-catch: best-effort remux; on any ffmpeg/IO failure the
        # original file is left untouched (tmp is cleaned up, never swapped in).
        return path


def _legacy_remux_command(
        path: Path, tmp: Path, ffmpeg: str, *,
        include_all_streams: bool) -> list[str]:
    """Build one legacy-to-MKV copy command, including input timestamp policy."""
    streams = (
        ["-c", "copy", "-map", "0", "-map_metadata", "0"]
        if include_all_streams
        else ["-c:v", "copy", "-c:a", "copy", "-map", "0:v", "-map", "0:a?"]
    )
    return [
        ffmpeg, "-nostdin", "-y",
        *remux_input_args(path, tmp.suffix),
        "-i", str(path), *streams, str(tmp),
    ]


def _remux_faststart(
        path: Path, ffmpeg: str, suffix: str, ) -> Path:
    """Rewrite an MP4/M4V in place with the moov atom moved to the front."""
    log.info("remux for streaming (faststart): %s", path.name)
    try:
        with exclusive_media_write(path, "faststart", suffix=suffix) as tmp:
            result = run_encode(
                [ffmpeg, "-nostdin", "-y", "-i", str(path),
                 "-c", "copy", "-map", "0", "-map_metadata", "0",
                 "-movflags", "+faststart", str(tmp)],
                timeout=28800, progress_path=tmp,
            )
            if result.returncode == 0 and tmp.exists() and tmp.stat().st_size >= path.stat().st_size * 0.8:
                replace_media_file(tmp, path)
            else:
                log.warning("faststart remux failed for %s: rc=%s; ffmpeg said: %s",
                            path.name, result.returncode, _ffmpeg_stderr_tail(result))
                tmp.unlink(missing_ok=True)
    except MediaWriteBusy as exc:
        log.info("faststart remux skipped for %s: %s", path.name, exc)
    except Exception:
        # allow-silent-catch: best-effort remux; on any ffmpeg/IO failure the
        # original file is left untouched (tmp is cleaned up, never swapped in).
        return path


def _remux_for_streaming(
        path: Path, ) -> Path:
    """Cheap copy-only remux pass.

    - MP4 missing faststart -> rewrite with moov at the front.
    - Legacy containers (.avi/.wmv/.mpg/.mpeg/.mov) -> remux into MKV.

    Returns the (possibly new) path.  No re-encoding occurs.
    """
    if not REMUX_FOR_STREAMING:
        return path
    if not path.exists():
        return path
    ffmpeg = require_tool("ffmpeg")
    suffix = path.suffix.lower()

    if suffix in _REMUX_FROM_EXTS:
        return _remux_legacy_container(path, ffmpeg)

    if suffix in (".mp4", ".m4v") and not _mp4_has_faststart(path):
        return _remux_faststart(path, ffmpeg, suffix)

    return path
