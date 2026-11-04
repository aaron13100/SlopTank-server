#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""I/O layer for the subtitle timing gate: read a video's own embedded
English text subtitle track as the trusted reference for
tools/lib/subtitle_timing_alignment.py's cue-start comparison.

Every other language sidecar of a video is checked against THAT video's own
embedded English track (never another episode's, never another release's),
because the diagnosed failure (The.Pitt.S01E01.720p...ja.srt byte-identical
to the 1080p release's .ja.srt -- a different cut) is exactly a sidecar that
was never checked against its own file at all.

Extraction is cached under cache_dir as "{video.stem}.s{index}.embedded.srt"
so a season's worth of audit/fix passes over multiple sidecars per video pay
the ffmpeg demux cost once per video, not once per sidecar.
"""

from __future__ import annotations

import json
import logging
import subprocess
from pathlib import Path

from lib.encode_cancellation import run_cancellable_capture
from lib.subtitle_language import looks_english
from lib.subtitle_timing_alignment import MIN_CUES, cue_starts_from_text

log = logging.getLogger("organize-media")

# ffprobe's codec_name values for text-based subtitle codecs. Image-based
# codecs (dvd_subtitle, hdmv_pgs_subtitle) carry no cue text to compare and
# are never candidate reference streams.
_TEXT_SUBTITLE_CODECS = {"ass", "subrip", "mov_text", "ssa", "webvtt"}

# A subtitle-only demux is a short sequential read of the container, so it
# runs at normal I/O priority. Measured 2026-09-23 on this box at load 15:
# The.Pitt.S01E03.720p (459MB) extracted in 5.8s unthrottled, while the same
# call under `taskpolicy -b` (and under `taskpolicy -d utility`) read almost
# nothing for 10 minutes to 2.6 hours: the throttle tier yields to every
# normal-priority reader on the shared volume and starves outright (the same
# starvation tests/test_encode_clamp_fairness.py documents for encodes).
# 300s is ~50x the measured cost; a timeout here is a real fault, not load.
EXTRACT_TIMEOUT_SECONDS = 300


def _probe_text_subtitle_streams(video: Path) -> list[dict]:
    """ffprobe's subtitle streams with codec_name, language, title, and the
    forced/hearing-impaired disposition flags -- fields the shared
    _ffprobe_subtitle_streams() in media_probe.py does not request, because
    its callers (extract/prefetch language gating) only ever need language.
    """
    try:
        result = run_cancellable_capture(
            ["ffprobe", "-v", "error", "-select_streams", "s",
             "-show_entries",
             "stream=index,codec_name:stream_tags=language,title:stream_disposition=forced,hearing_impaired",
             "-of", "json", str(video)],
            timeout=120,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("ffprobe failed for %s: rc=%s; ffprobe said: %s", video.name,
                        result.returncode, (result.stderr or "").strip()[-500:] or "<no stderr>")
            return []
        data = json.loads(result.stdout)
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        # The caller treats an empty list as "no reference available" and
        # falls back to the audio path; the cause is logged here.
        log.warning("ffprobe failed for %s: %s", video.name, exc)
        return []
    return data.get("streams", [])


def _select_english_text_stream(
        streams: list[dict], *, excluded_indexes: set[int] | None = None
        ) -> tuple[dict, bool] | None:
    """Pick the reference English text subtitle stream: prefer a
    non-hearing-impaired stream whose title does not say SDH, else any
    English text stream, else the only text stream when there is exactly
    one (a single unlabeled text track is very likely English on this
    library, and worth using as a reference; more than one unlabeled track
    is too ambiguous to guess).

    :returns: ``(stream, needs_language_check)``, or None when no candidate
        exists. ``needs_language_check`` is True only for the untagged/"und"
        fallback (the lone text stream with no eng/en tag): its extracted
        text was never actually confirmed to be English, so the caller must
        verify it with :func:`lib.subtitle_language.looks_english` before
        trusting it. A tagged eng/en stream is trusted as-is.
    """
    excluded_indexes = excluded_indexes or set()
    text = [s for s in streams
            if (s.get("codec_name") or "").lower() in _TEXT_SUBTITLE_CODECS]
    candidates = [s for s in text
                  if s.get("index") not in excluded_indexes
                  and not is_forced_subtitle_stream(s)]
    english = [s for s in candidates
               if (s.get("tags", {}).get("language") or "").strip().lower() in {"en", "eng"}]
    plain = [s for s in english
             if not (s.get("disposition") or {}).get("hearing_impaired")
             and "sdh" not in (s.get("tags", {}).get("title") or "").lower()]
    for group in (plain, english):
        if group:
            return group[0], False
    if len(text) == 1 and len(candidates) == 1:
        return text[0], True
    return None


def is_forced_subtitle_stream(stream: dict) -> bool:
    """Forced-only tracks are not dialogue references, including title-tagged tracks."""
    disposition = stream.get("disposition") or {}
    title = (stream.get("tags", {}).get("title") or "").strip().lower()
    return bool(disposition.get("forced")) or "forced" in title


def reference_starts_for(video: Path, cache_dir: Path) -> tuple[list[float], str] | None:
    """Cue start times (seconds) of `video`'s own embedded English text
    subtitle track, extracting and caching it if not already cached.

    :param video: The video file whose embedded English track is wanted.
    :param cache_dir: Directory holding (or to receive)
        "{video.stem}.s{index}.embedded.srt" extractions. Shared across
        every sidecar of the same video, and safe to reuse across runs --
        an existing cache entry is never re-extracted or deleted here.
    :returns: (cue_starts, reference_name), e.g. (starts, "embedded:s2"),
        or None when the video has no embedded English text subtitle track
        (including when the only candidate is an untagged/"und" stream
        whose extracted text does not actually read as English).
    """
    streams = _probe_text_subtitle_streams(video)
    excluded_indexes: set[int] = set()
    while True:
        selection = _select_english_text_stream(
            streams, excluded_indexes=excluded_indexes)
        if selection is None:
            return None
        stream, needs_language_check = selection
        index = stream.get("index")
        if index is None:
            return None

        # A cache entry is only valid after current stream metadata selects
        # that same track. Extracting by index reuses it when present, then
        # the cue-count and untagged-language checks below run on both cache
        # hits and fresh output.
        final_path = extract_subtitle_stream_cached(video, index, cache_dir)
        if final_path is None:
            return None
        text = final_path.read_text(encoding="utf-8", errors="ignore")
        starts = cue_starts_from_text(text)
        if len(starts) < MIN_CUES:
            log.warning(
                "sparse English text subtitle stream has %d cues (need %d), "
                "skipping as reference: %s stream %d",
                len(starts), MIN_CUES, video.name, index,
            )
            excluded_indexes.add(index)
            continue
        if needs_language_check and not looks_english(text):
            # Discard a rejected untagged extraction so the cache cannot
            # later bypass the language check.
            log.warning(
                "untagged text subtitle stream did not read as English, "
                "skipping as reference: %s stream %d", video.name, index,
            )
            final_path.unlink(missing_ok=True)
            excluded_indexes.add(index)
            continue
        return starts, f"embedded:s{index}"


def subtitle_extract_command(video: Path, index: int, out: Path,
                            *, ffmpeg: str = "ffmpeg") -> list[str]:
    """argv that demuxes absolute stream `index` of `video` to SRT at `out`.

    Deliberately NOT wrapped in taskpolicy: see EXTRACT_TIMEOUT_SECONDS.
    """
    return [ffmpeg, "-nostdin", "-y", "-v", "error",
            "-i", str(video), "-map", f"0:{index}", "-c:s", "srt", str(out)]


class SubtitleStreamExtractionError(RuntimeError):
    """Typed extraction failure for callers that must distinguish host faults."""

    def __init__(self, kind: str, detail: str, *, cause: BaseException | None = None,
                 returncode: int | None = None, stderr: str = ""):
        self.kind = kind
        self.cause = cause
        self.returncode = returncode
        self.stderr = stderr
        super().__init__(detail)


def _discard_partial(path: Path) -> None:
    try:
        path.unlink(missing_ok=True)
    except OSError as exc:
        log.warning("could not remove partial embedded-subtitle cache %s: %s",
                    path, exc, exc_info=True)


def require_cached_subtitle_stream(video: Path, index: int, cache_dir: Path,
                                   *, ffmpeg: str = "ffmpeg") -> Path:
    """Strict shared extraction API used by callers that must classify errors.

    The timing gate's legacy wrapper below still converts this exception to
    ``None`` and logs it; translation can preserve the real failure cause.
    """
    video, cache_dir = Path(video), Path(cache_dir)
    final_path = cache_dir / f"{video.stem}.s{index}.embedded.srt"
    part_path = final_path.with_name(final_path.name + ".part.srt")
    try:
        cache_dir.mkdir(parents=True, exist_ok=True)
        if final_path.exists() and final_path.stat().st_size > 0:
            return final_path
    except OSError as exc:
        raise SubtitleStreamExtractionError(
            "host_io", f"cannot inspect subtitle cache: {exc}", cause=exc) from exc
    command = subtitle_extract_command(video, index, part_path, ffmpeg=ffmpeg)
    try:
        result = run_cancellable_capture(command, timeout=EXTRACT_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError(
            "timeout", f"embedded subtitle extraction timed out after {EXTRACT_TIMEOUT_SECONDS}s",
            cause=exc) from exc
    except FileNotFoundError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError("tool_unavailable", str(exc), cause=exc) from exc
    except PermissionError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError("tool_unavailable", str(exc), cause=exc) from exc
    except OSError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError("host_io", str(exc), cause=exc) from exc
    except subprocess.SubprocessError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError("host_process", str(exc), cause=exc) from exc
    try:
        produced = part_path.exists() and part_path.stat().st_size > 0
    except OSError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError(
            "host_io", f"cannot inspect extracted subtitle: {exc}", cause=exc) from exc
    if result.returncode != 0 or not produced:
        stderr_tail = (result.stderr or "").strip()[-500:]
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError(
            "media_decode", f"embedded subtitle extraction failed: rc={result.returncode}; "
            f"{stderr_tail or '<no stderr>'}", returncode=result.returncode,
            stderr=stderr_tail)
    try:
        # allow-bare-rename: publishes extracted subtitle bytes under the cache-owned path, not video media.
        # Keep final and in-flight names inside this function's subtitle cache.
        part_path.replace(final_path)
    except OSError as exc:
        _discard_partial(part_path)
        raise SubtitleStreamExtractionError(
            "host_io", f"cannot publish subtitle cache: {exc}", cause=exc) from exc
    return final_path


def extract_subtitle_stream_cached(video: Path, index: int, cache_dir: Path) -> Path | None:
    """Extract absolute stream `index` of `video` as SRT into
    cache_dir/"{video.stem}.s{index}.embedded.srt" (atomic via a ".part.srt"
    in-flight name), or return the existing cache entry unchanged.

    The single extraction path shared by this timing gate and the Muse
    Japanese translation driver, so one demux per video serves both.

    :param video: Source video file.
    :param index: Absolute ffprobe stream index of a text subtitle stream.
    :param cache_dir: Cache directory; created if missing.
    :returns: Path of the cached SRT, or None when extraction failed (the
        cause is logged with the video name and ffmpeg's stderr).
    """
    try:
        return require_cached_subtitle_stream(video, index, cache_dir)
    except SubtitleStreamExtractionError as exc:
        log.warning("embedded-subtitle extraction failed for %s: %s", video.name, exc)
        return None
