#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Bring quiet audio tracks up to -16 LUFS (EBU R128) via ffmpeg loudnorm."""

from __future__ import annotations

import logging
import shutil  # test seam: tests patch audio_normalize.shutil.which (this same
               # global module); require_tool resolves through it, so it stays.
import subprocess
from dataclasses import dataclass
from lib.media_identity import replace_media_file
from pathlib import Path

from lib.organize_media_config import (
    NORMALIZE_AUDIO, NORMALIZE_AUDIO_THRESHOLD_DB)
from lib.audio_defaults import _is_tv_playable_audio, _probe_audio_streams
from lib.media_probe import _get_mean_volume, _ffmpeg_stderr_tail
from lib.encode_priority import run_encode
from lib.media_write_lock import MediaWriteBusy, exclusive_media_write
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")

# The rate the re-encoded track is pinned to.
#
# loudnorm resamples to 192 kHz internally, and with no -ar the AAC encoder
# follows it and clamps to its own 96 kHz maximum. Owner clip 32 proved AAC at
# 96 kHz is SILENT on the Panasonic TX-LF37E30, so an unpinned run took a file
# that satisfied the TV rule and made it violate that rule, in place, on the
# only copy (observed 2026-09-06 on "The Martian ... Extended.mkv", whose
# 48 kHz E-AC3 came back as 96 kHz AAC).
#
# Pinning is not a downgrade: the sources this pass touches are 48 kHz already,
# so the unpinned command was upsampling them for nothing. Channel count is
# deliberately NOT constrained here -- downmixing would destroy the 5.1 mix on
# the only copy, which PROJECT_DIRECTION rejects outright.
#
# tests/test_audio_normalize.py ties this to organize_media_config's settled
# TV_AUDIO_SAMPLE_RATES, so a change to the TV rule fails the build here.
NORMALIZE_SAMPLE_RATE = 48000


# The widest track this pass may re-encode.
#
# PROJECT_DIRECTION settled 2026-09-04 that the conversion is ADDITIVE and
# "the 5.1 mix is never re-encoded, because the source .mkv is deleted by the
# pipeline and that loss would be irreversible". This pass rewrites the only
# copy in place, so it is exactly the pass that could commit that loss. A
# ceiling here makes it impossible by construction rather than by whoever
# reviews the next edit to the ffmpeg command: a surround track is skipped,
# which leaves the file quiet and recoverable, instead of re-encoded, which is
# neither.
#
# Nothing is given up by this. The track a viewer hears is the derived AAC
# stereo one the conversion adds and marks default, and that is the track this
# pass now measures and normalizes.
MAX_NORMALIZABLE_CHANNELS = 2


@dataclass(frozen=True)
class NormalizableTrack:
    """Which track the loudness pass may re-encode, and why not when it may not.

    Returned rather than a bare index so the caller can say WHICH of the three
    reasons applied. They look alike from the outside and are not alike: an
    unreadable file is a fault to chase, a surround default is this pass
    correctly declining, and a malformed channel count is a third thing again.
    A None index with no reason attached is what makes a skipped file
    unexplainable in the log.
    """

    index: int | None
    reason: str = ""

    def __bool__(self) -> bool:
        return self.index is not None


def _normalizable_track(streams: list[dict]) -> NormalizableTrack:
    """The one track this pass may re-encode, or why there is none.

    The default-disposition track is what a player selects, so it is the track
    whose loudness the viewer experiences and the only one worth re-encoding.
    Falling back to the first stream keeps single-track files working, since a
    file with one audio stream plays that stream whether or not it is flagged.

    :param streams: Raw ffprobe audio streams in file order, as
        :func:`audio_defaults._probe_audio_streams` returns them.
    :returns: A :class:`NormalizableTrack`; falsy when no track may be touched.
    """
    if not streams:
        return NormalizableTrack(None, "its audio streams could not be read")
    index = next(
        (i for i, s in enumerate(streams)
         if (s.get("disposition") or {}).get("default")),
        0)
    raw_channels = streams[index].get("channels")
    try:
        channels = (
            int(raw_channels)
            if isinstance(raw_channels, (int, str))
            and not isinstance(raw_channels, bool)
            else 0)
    except ValueError:
        channels = 0
    if channels <= 0:
        return NormalizableTrack(
            None, f"track a:{index} reports an unusable channel count "
                  f"({raw_channels!r}), so it is left alone")
    if channels > MAX_NORMALIZABLE_CHANNELS:
        return NormalizableTrack(
            None, f"the default of {len(streams)} audio stream(s) is a "
                  f"{channels}-channel mix, which is never re-encoded")
    return NormalizableTrack(index)


def _rejection_reason(tmp_path: Path, audio_index: int) -> str | None:
    """Why the normalized file must NOT replace the original, or None if it may.

    This pass overwrites the only copy, so it verifies its own output before
    committing it, the same way fix-tv-audio-catchup verifies the tracks it
    adds. Without this the pass could only be trusted as far as its ffmpeg
    command was reviewed, which is what let an unpinned sample rate rewrite a
    TV-playable file into a silent one (see NORMALIZE_SAMPLE_RATE).

    The check asks :func:`audio_defaults._is_tv_playable_audio`, the one place
    the owner's clip results are encoded, rather than re-checking a dimension
    or two here. A sample-rate-only gate passed AAC 5.1 at 48 kHz, and clip 02
    proved that is silent in MP4 -- which is why the container is part of the
    question and why this function must not answer it alone.

    Fails closed: an unprobeable result is a rejection, never a pass.

    :param tmp_path: The freshly encoded candidate file.
    :param audio_index: Audio-relative index of the track that was re-encoded.
    :return: A human-readable reason to reject, or None to accept.
    """
    streams = _probe_audio_streams(tmp_path)
    if not streams:
        return "could not probe the normalized file"
    if audio_index >= len(streams):
        return (f"the re-encoded track a:{audio_index} is missing from the "
                f"result, which has {len(streams)} audio stream(s)")
    stream = streams[audio_index]
    if not _is_tv_playable_audio(stream, tmp_path.suffix):
        return (f"re-encoded track is {stream.get('codec_name')} at "
                f"{stream.get('sample_rate')} Hz, {stream.get('channels')}ch, "
                f"in {tmp_path.suffix}, which the television cannot play")
    return None


def _normalization_command(
        ffmpeg: str,
        path: Path,
        tmp_path: Path,
        audio_index: int) -> list[str]:
    """Build the whole-file rewrite command for one selected audio track."""
    cmd = [
        ffmpeg, "-nostdin", "-y", "-i", str(path),
        "-map", "0",
        "-c", "copy",
        f"-c:a:{audio_index}", "aac",
        f"-b:a:{audio_index}", "192k",
        f"-ar:a:{audio_index}", str(NORMALIZE_SAMPLE_RATE),
        f"-filter:a:{audio_index}", "loudnorm=I=-16:TP=-1.5:LRA=11",
    ]
    if path.suffix.lower() in (".mp4", ".m4v"):
        cmd += ["-movflags", "+faststart"]
    cmd.append(str(tmp_path))
    return cmd


def _normalization_target(
        path: Path,
        loudness_measured_source_indexes: frozenset[int]) -> tuple[str, int] | None:
    """Return the encoder and audio-relative index eligible for normalization.

    :raises MediaToolMissingError: ffmpeg cannot be resolved.
    """
    if not NORMALIZE_AUDIO:
        return None
    streams = _probe_audio_streams(path)
    selected = _normalizable_track(streams)
    if not selected:
        log.info("audio normalize skipped for %s: %s", path.name, selected.reason)
        return None
    index = selected.index
    source_index = streams[index].get("index")
    if source_index in loudness_measured_source_indexes:
        log.info(
            "audio normalize deferred for %s: streaming plan already measured "
            "source stream %s for its derived AAC default",
            path.name, source_index)
        return None
    ffmpeg = require_tool("ffmpeg")
    mean_vol = _get_mean_volume(path, audio_index=index)
    if mean_vol is None or mean_vol > NORMALIZE_AUDIO_THRESHOLD_DB:
        return None
    return ffmpeg, index


def normalize_audio(
        path: Path,
        *,
        loudness_measured_source_indexes: frozenset[int] = frozenset()) -> bool:
    """Normalize audio if the file's mean volume is below threshold.

    Uses FFmpeg loudnorm filter to bring quiet files to -16 LUFS (EBU R128).
    Video and subtitle streams are copied untouched; only the selected default
    stereo track is re-encoded. When the streaming plan has already measured a
    source for a derived AAC companion, that source is left for the companion
    encode: this pass neither decodes it again nor rewrites it first.
    """
    target = _normalization_target(path, loudness_measured_source_indexes)
    if target is None:
        return False
    ffmpeg, index = target

    try:
        with exclusive_media_write(path, "normalizing") as tmp_path:
            cmd = _normalization_command(ffmpeg, path, tmp_path, index)
            # Whole-file rewrite: needs the playback clamp and the stall watchdog,
            # both of which run_encode owns. It used to be a bare subprocess.run on
            # a flat 30 min budget, which on a contended disk is under a fifth of
            # what a large file honestly takes.
            result = run_encode(cmd, timeout=28800, progress_path=tmp_path)
            if result.returncode != 0 or not tmp_path.exists():
                log.warning("audio normalize failed for %s: rc=%s; ffmpeg said: %s",
                            path.name, result.returncode, _ffmpeg_stderr_tail(result))
                return False
            reason = _rejection_reason(tmp_path, index)
            if reason is not None:
                log.warning("audio normalize rejected for %s: %s; original left untouched",
                            path.name, reason)
                return False
            replace_media_file(tmp_path, path)
            return True
    except MediaWriteBusy as exc:
        log.info("audio normalize skipped for %s: %s", path.name, exc)
        return False
    except Exception:
        # Best-effort normalize: the caller treats False as "not normalized,
        # keep going". Logged with the traceback rather than swallowed, because
        # replace_media_file runs inside this block and a failure there is a
        # half-finished swap of the only copy, not a routine skip.
        log.exception("audio normalize failed for %s; original left untouched",
                      path.name)
        return False
