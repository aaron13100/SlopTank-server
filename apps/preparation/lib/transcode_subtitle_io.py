# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Fail-safe extraction and cleanup of subtitle sidecars the MP4 target
cannot carry, and the cue-timing measurement that decides which text tracks
those are."""

from __future__ import annotations

import json
import logging
import subprocess
from dataclasses import dataclass
from pathlib import Path

from lib.encode_cancellation import run_cancellable_capture
from lib.encode_priority import run_encode
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.load_scaled_timeout import load_scaled_timeout
from lib.media_probe import _ffmpeg_stderr_tail
from lib.transcode_command_plan import (
    MP4_TEXT_GAP_LIMIT_SECONDS,
    _TEXT_SUBTITLE_CODECS,
    _sidecar_subtitle_targets,
)
from lib.transcode_types import TranscodeFailedError
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")

#: The base (unscaled) bound for one ffprobe HEADER read here (stream
#: listing); scaled per call by load_scaled_timeout, like
#: lib.mp4_start_alignment's probe reads. Packet enumeration is bounded
#: separately by stream_copy_outer_timeout_seconds because it walks the
#: whole container.
_PROBE_BASE_TIMEOUT_SECONDS = 120

#: mov_text tracks carry structural "empty" samples that hold each cue gap:
#: one-tick samples between cues in a collapsed track (c298) and one long
#: sample covering the leading silence in a healthy one. Both are exactly
#: TWO bytes of tx3g (a zero text length), while a real cue is the length
#: marker plus its text -- measured 2026-09-29 on both shapes: empties are
#: size 2, the shortest real cue 10. A subrip track in Matroska has no
#: empties at all. A packet must clear BOTH this and
#: _MIN_CUE_DURATION_SECONDS to count as a cue: duration alone would let a
#: healthy track's 84s leading empty masquerade as a cue at 0.000s.
_MAX_EMPTY_PACKET_BYTES = 2
#: A real cue lasts at least a few hundred milliseconds; this drops the
#: one-tick empties of a collapsed track even where their size is unusual.
_MIN_CUE_DURATION_SECONDS = 0.05


@dataclass(frozen=True)
class TextTrackCueFacts:
    """One text-subtitle track's timing, measured from its own packets.

    :ivar index: File-wide ffprobe stream index.
    :ivar language: The track's language tag, or None when untagged.
    :ivar first_cue_start: First cue's presentation start, seconds.
    :ivar cue_count: Real cues in the track (structural empties excluded).
    :ivar max_gap_seconds: Longest leading or inter-cue gap, seconds -- the
        number the mp4 muxer's int32 stts range has to hold (see
        ``MP4_TEXT_GAP_LIMIT_SECONDS``).
    """

    index: int
    language: str | None
    first_cue_start: float
    cue_count: int
    max_gap_seconds: float


def _run_json_ffprobe(path: Path, args: list[str], *,
                      timeout: float | None = None) -> dict:
    """One read-only ffprobe query as parsed JSON.

    :param timeout: Bound for this read. Callers that enumerate packets are
        demuxing the WHOLE container (Matroska has no sample tables to read
        instead), so they must pass the size-derived
        ``stream_copy_outer_timeout_seconds``; a flat or load-scaled bound
        cannot cover a full multi-GB read under the background-I/O tier
        (i261, finding section 4 -- the same conclusion
        subtitle_embed._normalize_srt_bytes records for its stdout reads).
        Defaults to the load-scaled header-read bound, which is enough for a
        stream listing.
    :raises TranscodeFailedError: ffprobe cannot run, times out, exits
        nonzero, or prints unparseable output. A probe that cannot be
        read must never read as "no overflow": that is the same
        silent-success path that let The Martian's collapsed track through
        (c298), and the conversion's per-file failure boundary already
        handles a raise.
    :raises MediaToolMissingError: ffprobe cannot be resolved.
    """
    ffprobe = require_tool("ffprobe")
    if timeout is None:
        timeout = load_scaled_timeout(_PROBE_BASE_TIMEOUT_SECONDS)
    try:
        result = run_cancellable_capture(
            [ffprobe, "-v", "error", *args, "-of", "json", str(path)],
            timeout=timeout,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise TranscodeFailedError(
            f"cannot read subtitle timing facts for {path.name}: {exc}") from exc
    if result.returncode != 0:
        raise TranscodeFailedError(
            f"cannot read subtitle timing facts for {path.name}: "
            f"ffprobe rc={result.returncode}: {(result.stderr or '')[-500:]}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise TranscodeFailedError(
            f"cannot read subtitle timing facts for {path.name}: "
            f"ffprobe produced unparseable output: {exc}") from exc


def _parse_float(value) -> float | None:
    """Best-effort float parse for an ffprobe field that may be "N/A"."""
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _parse_int(value) -> int | None:
    """Best-effort int parse for an ffprobe field that may be "N/A"."""
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def probe_text_track_cue_facts(path: Path) -> dict[int, TextTrackCueFacts]:
    """Every text-subtitle track's cue timing in `path`, keyed by stream index.

    Reads each track's own packets (pts + duration), which is what the mp4
    muxer will re-derive its stts gaps from. Enumerating packets demuxes the
    whole container, so the read is bounded by the size-derived
    ``stream_copy_outer_timeout_seconds`` (i261). A file with no
    text-subtitle track returns an empty dict -- a real result, never a
    failure, and no packet read runs for it at all. All subtitle packets are
    read in one demux and grouped by absolute stream index. Bitmap packets
    are discarded, so interleaved text/bitmap tracks keep their identities
    without reading the entire movie once per text language.
    """
    streams = _run_json_ffprobe(path, [
        "-select_streams", "s", "-show_entries",
        "stream=index,codec_name:stream_tags=language",
    ]).get("streams") or []
    text_streams = {
        stream["index"]: stream for stream in streams
        if (stream.get("codec_name") or "").lower() in _TEXT_SUBTITLE_CODECS
        and stream.get("index") is not None
    }
    if not text_streams:
        return {}
    try:
        whole_read_bound = stream_copy_outer_timeout_seconds(
            path.stat().st_size)
    except OSError as exc:
        raise TranscodeFailedError(
            f"cannot read subtitle timing facts for {path.name}: {exc}") from exc
    packets = _run_json_ffprobe(path, [
        "-select_streams", "s", "-show_entries",
        "packet=stream_index,pts_time,duration_time,size",
    ], timeout=whole_read_bound).get("packets") or []
    spans_by_index: dict[int, list[tuple[float, float]]] = {
        index: [] for index in text_streams
    }
    for packet in packets:
        index = _parse_int(packet.get("stream_index"))
        if index not in spans_by_index:
            continue
        pts = _parse_float(packet.get("pts_time"))
        duration = _parse_float(packet.get("duration_time"))
        size = _parse_int(packet.get("size"))
        if pts is None or duration is None:
            continue
        if size is not None and size <= _MAX_EMPTY_PACKET_BYTES:
            continue
        if duration < _MIN_CUE_DURATION_SECONDS:
            continue
        spans_by_index[index].append((pts, pts + duration))
    facts: dict[int, TextTrackCueFacts] = {}
    for index, stream in text_streams.items():
        spans = spans_by_index[index]
        if not spans:
            continue
        max_gap = spans[0][0]
        previous_end = spans[0][1]
        for start, end in spans[1:]:
            max_gap = max(max_gap, start - previous_end)
            previous_end = end
        facts[index] = TextTrackCueFacts(
            index=index,
            language=stream.get("tags", {}).get("language"),
            first_cue_start=spans[0][0],
            cue_count=len(spans),
            max_gap_seconds=max(max_gap, 0.0),
        )
    return facts


def extract_mp4_incompatible_subtitle_sidecars(
        path: Path, info: dict, *, encode=None,
        probe_facts=None) -> list[Path]:
    """Extract every subtitle track the MP4 target cannot carry, or fail
    before conversion.

    Two classes: bitmap tracks (no mp4 codec exists) and text tracks whose
    cue gaps overflow the mov_text muxer's int32 stts range -- mapping one
    of those into an mp4 silently collapses the track onto the film's
    opening (c298; ``MP4_TEXT_GAP_LIMIT_SECONDS``), so it goes to an .srt
    sidecar Jellyfin reads beside the video instead.

    The text-gap verdict is measured here, once per file before any command
    is built, and recorded on ``info`` as ``mp4_text_gap_overflow`` so the
    pure command builder maps the same streams this extracted (the
    measured-facts-on-info pattern the audio start alignment uses). A probe
    that cannot be read raises rather than assuming "representable".
    """
    if probe_facts is None:
        probe_facts = probe_text_track_cue_facts
    facts = probe_facts(path)
    info["mp4_text_gap_overflow"] = {
        index: track.max_gap_seconds
        for index, track in facts.items()
        if track.max_gap_seconds > MP4_TEXT_GAP_LIMIT_SECONDS
    }
    targets = _sidecar_subtitle_targets(path, info)
    if not targets:
        return []
    ffmpeg = require_tool("ffmpeg")
    text_indexes = {
        stream.get("index") for stream in (info.get("subtitle_streams") or [])
        if stream.get("codec") in _TEXT_SUBTITLE_CODECS
    }
    command = [ffmpeg, "-nostdin", "-y", "-i", str(path)]
    for index, target in targets:
        codec = "srt" if index in text_indexes else "copy"
        command += ["-map", f"0:{index}", "-c:s", codec, str(target)]
    # Demuxing to reach the subtitle tracks reads the whole container, so the
    # outer bound is size-derived (stream_copy_outer_timeout_seconds, i261
    # finding section 4), and the FIRST sidecar is the progress signal for the
    # stall watchdog: ffmpeg writes all targets interleaved while it demuxes,
    # so that one file's growth separates "slow" from "stuck" (precedent:
    # subtitle_pipeline's English-SRT extraction).
    result = (encode or run_encode)(
        command,
        timeout=stream_copy_outer_timeout_seconds(path.stat().st_size),
        progress_path=targets[0][1])
    if result.returncode != 0:
        for _, target in targets:
            target.unlink(missing_ok=True)
        raise TranscodeFailedError(
            f"mp4-incompatible subtitle extraction failed for {path.name}: "
            f"rc={result.returncode}; ffmpeg said: {_ffmpeg_stderr_tail(result)}",
        )
    written = []
    for _, target in targets:
        if target.exists() and target.stat().st_size > 0:
            written.append(target)
        else:
            target.unlink(missing_ok=True)
    log.info(
        "extracted %d mp4-incompatible subtitle sidecar(s) from %s: %s",
        len(written), path.name,
        ", ".join(target.name for target in written) or "none",
    )
    return written


def discard_sidecars(sidecars: list[Path]) -> None:
    """Remove sidecars whose subsequent conversion failed."""
    for sidecar in sidecars:
        try:
            sidecar.unlink(missing_ok=True)
        except OSError as exc:
            log.warning(
                "could not remove orphaned subtitle sidecar %s: %s",
                sidecar.name, exc,
            )
