#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Read-only ffprobe-based inspection of a media file.

No mutation: codec/duration/volume/keyframe-spacing measurements used by the
remux, transcode, and audio-normalize stages to decide whether work is needed.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
from pathlib import Path

from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.encode_priority import run_encode
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.media_duration import get_media_duration
from lib.media_probe_cache import run_cached_probe
from lib.organize_media_config import MIN_MOVIE_SIZE_BYTES, MIN_MOVIE_SIZE_MB, MIN_MOVIE_DURATION_SECONDS
from lib.probe_stream_parser import _parse_probe_streams
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")


# ffmpeg redraws these in place while it works. They are status, never a
# reason, and because they are written last they are exactly what a short tail
# keeps. Anchored at the start of the line so an error that merely mentions a
# frame number is not mistaken for progress.
_FFMPEG_PROGRESS_RE = re.compile(r"^(?:frame|size)=\s*\S")


def _ffmpeg_stderr_tail(result: subprocess.CompletedProcess, lines: int = 8) -> str:
    """Compact tail of an ffmpeg/ffprobe run's stderr for failure logs.

    Every ffmpeg failure log must carry the actual error text, not just the
    return code -- an rc-only warning forced a manual repro to diagnose the
    2026-07-23 Misfits backfill failure (78 files, rc=234, cause invisible).

    Widening that to a three-line tail was not enough, because the last three
    lines of a failed run are the same three every time and none of them is the
    cause: the redrawn progress counter, the codec's closing Qavg, and
    "Conversion failed!". Observed 2026-09-06, the complete diagnosis available
    for three failed attempts on "The Martian (2015) ... Extended.mkv" was

        rc=234; ffmpeg said: frame=    0 fps=0.0 q=-0.0 Lsize=       0KiB
        time=N/A bitrate=N/A speed=N/A elapsed=0:00:01.98    ;
        [aac @ 0x7fc7800155c0] Qavg: 163.448; Conversion failed!

    so progress lines are dropped and the budget is spent on lines that can
    carry a reason. They are dropped rather than merely deprioritised because a
    single 200-column progress line would otherwise fill the whole tail.

    Falls back to the raw tail when progress is ALL there is, since reporting
    noise beats reporting nothing.

    :param result: The finished ffmpeg/ffprobe process.
    :param lines: How many diagnostic lines to keep, newest last.
    :returns: A single log-safe line, or '<no stderr>' when there was none.
    """
    raw = result.stderr or ""
    if isinstance(raw, (bytes, bytearray)):
        # capture_output without text=True yields bytes, and the error path is
        # the worst place to raise a second error: joining bytes used to throw
        # a TypeError that replaced a diagnosable failure with a logger crash.
        raw = raw.decode("utf-8", "replace")
    # Progress is separated by carriage returns, not newlines, so splitlines()
    # alone leaves the whole redraw history glued into one enormous "line".
    candidates = [ln.strip() for ln in raw.replace("\r", "\n").splitlines() if ln.strip()]
    diagnostic = [ln for ln in candidates if not _FFMPEG_PROGRESS_RE.match(ln)]
    return "; ".join((diagnostic or candidates)[-lines:]) or "<no stderr>"


def _ffprobe_subtitle_streams(path: Path) -> list[dict]:
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cached_probe(
            [
                ffprobe,
                "-v",
                "error",
                "-show_entries",
                "stream=index,codec_type:stream_tags=language,title",
                "-of",
                "json",
                str(path),
            ],
            # t_260920_052208_901 sibling sweep: this reads container
            # metadata only, never decodes frames, so it does not scale
            # with file size. Measured 1.4s live against a real 526 MB
            # library file on this host under loadavg 87-101; 60s is
            # ~40x margin.
            path=path,
            timeout=60,
            runner=run_cancellable_capture,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("could not probe subtitle streams of %s: rc=%s; %s",
                        path.name, result.returncode, _ffmpeg_stderr_tail(result))
            return []
        data = json.loads(result.stdout)
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not probe subtitle streams of %s: %s", path.name, exc)
        return []
    streams = []
    for stream in data.get("streams", []):
        if stream.get("codec_type") != "subtitle":
            continue
        streams.append(stream)
    return streams


def _is_real_movie_file(path: Path) -> bool:
    """Return False if the file is too small or too short to be a real movie."""
    size = path.stat().st_size
    if size < MIN_MOVIE_SIZE_BYTES:
        log.info("reject (file too small: %.1fMB < %dMB): %s",
                 size / (1024 * 1024), MIN_MOVIE_SIZE_MB, path.name)
        return False
    duration = get_media_duration(path)
    if duration is not None and duration < MIN_MOVIE_DURATION_SECONDS:
        log.info("reject (duration too short: %.0fs < %ds): %s",
                 duration, MIN_MOVIE_DURATION_SECONDS, path.name)
        return False
    return True


def _probe_primary_audio_language_code(path: Path) -> str:
    """Return the raw (untranslated) language code of the first tagged audio stream."""
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cached_probe(
            [
                ffprobe,
                "-v",
                "error",
                "-select_streams",
                "a",
                "-show_entries",
                "stream_tags=language",
                "-of",
                "json",
                str(path),
            ],
            # t_260920_052208_901 sibling sweep: metadata-only, same
            # measurement basis as _ffprobe_subtitle_streams above (1.4s
            # live on a real 526 MB file under loadavg 87-101).
            path=path,
            timeout=60,
            runner=run_cancellable_capture,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("could not probe audio language of %s: rc=%s; %s",
                        path.name, result.returncode, _ffmpeg_stderr_tail(result))
            return ""
        data = json.loads(result.stdout)
        for stream in data.get("streams", []):
            code = (stream.get("tags") or {}).get("language", "")
            if code and code.lower() not in ("und", "zxx"):
                return code
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not probe audio language of %s: %s", path.name, exc)
        return ""
    return ""


def _get_mean_volume(path: Path, audio_index: int = 0) -> float | None:
    """Measure the mean volume of one of a media file's audio streams.

    :param path: The media file to measure.
    :param audio_index: Audio-RELATIVE stream index, i.e. the ``n`` in
        ``0:a:n``. Defaults to the first audio stream, which is what every
        caller wanted while files had one. A caller that is about to re-encode
        a specific track must pass THAT track's index: after the additive
        conversion the track a viewer actually hears is rarely a:0, and
        measuring one track while encoding another lets a loud source mix
        suppress normalization of a quiet stereo one (and the reverse).
    :returns: Mean volume in dB, or None when it cannot be measured.
    """
    ffmpeg = require_tool("ffmpeg")
    try:
        # volumedetect decodes the whole audio stream, so this is a full read of
        # a multi-gigabyte file on the volume Jellyfin streams from -- exactly
        # the shape run_encode exists to keep away from playback. It used to be
        # a bare subprocess.run, which meant no clamp at launch and no guard to
        # apply one when a viewer turned up mid-measurement.
        #
        # i261 (finding section 4): the null muxer writes no output file, so
        # the stall watchdog has nothing to watch and a flat wall-clock bound
        # is this call's ONLY bound -- the 600s one below SIGKILLed 16 honest
        # probes in two days under the pipeline's background-I/O tier, failing
        # each victim file's whole conversion. The size-derived, floor-900s,
        # 20x-headroom number from stream_copy_outer_timeout_seconds is the
        # sanctioned no-watchdog bound (see its docstring).
        #
        # t_260921_182721_855: progress_report=True gives it a watchdog after
        # all -- ffmpeg's own -progress report, judged by the decoded audio
        # position (dense: it advances with every read), so a stall is caught
        # by progress and a starved-but-moving probe is never killed on time
        # alone; the size-derived number now only paces the recheck.
        result = run_encode(
            [ffmpeg, "-nostdin", "-i", str(path), "-map", f"0:a:{audio_index}",
             "-af", "volumedetect", "-f", "null", "/dev/null"],
            timeout=stream_copy_outer_timeout_seconds(path.stat().st_size),
            progress_report=True,
        )
        for line in result.stderr.splitlines():
            m = re.search(r"mean_volume:\s*([-\d.]+)\s*dB", line)
            if m:
                return float(m.group(1))
        log.warning("could not measure audio volume of %s: rc=%s; %s",
                    path.name, result.returncode, _ffmpeg_stderr_tail(result))
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not measure audio volume of %s: %s", path.name, exc)
    return None


def _probe_video_audio(path: Path) -> dict | None:
    """Return stream/container facts for the file, or None on failure.

    Keys: video_codec, video_height and video_width (int px or None),
    video_color_transfer / video_color_primaries / video_color_space (ffprobe
    names such as 'smpte2084', or None), video_profile (lowercase
    str or None), video_pix_fmt (str or None), video_level (int or None),
    audio_codec, audio_sample_rate (int Hz or None), audio_channels (int or None),
    audio_bit_rate_total (int bps summed over every audio stream, or None),
    container (ffprobe format_name, comma list), subtitle_streams (list of
    {'index': int, 'codec': str, 'language': str|None, 'title': str|None,
    'forced': bool, 'default': bool} for every subtitle stream, in file order).
    Codec values are normalized to lowercase short names (e.g. 'h264', 'hevc',
    'aac', 'ac3', 'subrip', 'hdmv_pgs_subtitle').  Audio fields describe the
    first audio stream only.
    """
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cached_probe(
            [ffprobe, "-v", "error",
             "-show_entries",
             "stream=index,codec_type,codec_name,width,height,sample_rate,channels,bit_rate"
             ",profile,level,pix_fmt,color_transfer,color_primaries,color_space"
             ":stream_tags=language,title"
             ":stream_disposition=default,forced"
             ":format=format_name,bit_rate",
             "-of", "json", str(path)],
            # A COLD read of a large library file can itself take a long time
            # on this host (measured 2026-09-06: 29-66s for 2.8-10.5 GB files
            # on first touch, 0.3s on every touch after); 30s silently
            # misreported those as unprobeable before this file's own reads
            # had ever landed.
            path=path,
            timeout=300,
            runner=run_cancellable_capture,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("could not probe video/audio of %s: rc=%s; %s",
                        path.name, result.returncode, _ffmpeg_stderr_tail(result))
            return None
        data = json.loads(result.stdout)
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not probe video/audio of %s: %s", path.name, exc)
        return None
    return _parse_probe_streams(data)


def _max_keyframe_gap(path: Path, sample_seconds: int = 240) -> float | None:
    """Largest gap (seconds) between consecutive video keyframes in the opening window.

    Returns the max keyframe-to-keyframe interval found in the first
    ``sample_seconds`` of the file, or None if it cannot be measured (probe
    error, fewer than two keyframes sampled). A missing ffprobe binary raises
    instead of reading as "no gap found".

    Keyframe packet flags supply these timestamps without decoding video
    frames. The HLS segmenter cuts on the same packet flags, so decoding the
    source merely to identify them costs CPU without adding a timing fact.

    Only the opening window is sampled: reading every keyframe in a full movie reads
    the whole file, and a too-long GOP from a default encode is already visible in the
    first few minutes.  Limitation: a file with a short GOP early that degrades later
    would be under-reported -- acceptable because encoder GOP structure is uniform in
    practice.  Used to flag direct-play files that still need re-encoding so Jellyfin's
    HLS segmenter can cut on keyframes (see TRANSCODE_KEYINT_SECONDS).
    """
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cached_probe(
            [ffprobe, "-v", "error", "-select_streams", "v:0",
             "-show_entries", "packet=pts_time,flags",
             "-of", "json", "-read_intervals", f"%{sample_seconds}", str(path)],
            path=path,
            timeout=120,
            runner=run_cancellable_capture,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("could not measure keyframe spacing of %s: rc=%s; %s",
                        path.name, result.returncode, _ffmpeg_stderr_tail(result))
            return None
        times: list[float] = []
        for packet in json.loads(result.stdout).get("packets", []):
            if "K" not in str(packet.get("flags") or ""):
                continue
            try:
                times.append(float(packet.get("pts_time")))
            except (TypeError, ValueError):
                continue  # "N/A" or malformed timestamp
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not measure keyframe spacing of %s: %s", path.name, exc)
        return None
    if len(times) < 2:
        return None
    times.sort()
    return max(b - a for a, b in zip(times, times[1:]))
