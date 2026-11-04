#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Environment-driven configuration for the organize-media pipeline.

All tunables (library paths, feature flags, transcode/remux/subtitle-sync
knobs) live here so every stage of the pipeline -- and any external tool that
loads organize-media.py to reuse a piece of it -- reads the same values.
"""

from __future__ import annotations

import os
from pathlib import Path


class MediaConfigError(ValueError):
    """An environment override for a media-config path is invalid."""


def _env_absolute_path_override(env_var: str, default: Path) -> Path:
    """Resolve a path from an environment override, defaulting when unset.

    :param env_var: name of the environment variable to read.
    :param default: value to use when ``env_var`` is unset or empty, so that
        behaviour with the variable unset is byte-identical to a fixed
        default.
    :returns: ``default``, or the overridden path when ``env_var`` is set.
    :raises MediaConfigError: if ``env_var`` is set to a non-absolute path.
        A relative override would be silently joined onto the process's
        current working directory, which differs by caller (cron, a test
        runner, a manually launched shell) and would resolve to a different
        location each time instead of failing loudly.
    """
    raw = os.environ.get(env_var)
    if not raw:
        return default
    candidate = Path(raw)
    if not candidate.is_absolute():
        raise MediaConfigError(f"{env_var}={raw!r} must be an absolute path")
    return candidate


def media_tool_override(name: str) -> str | None:
    """Return the environment override for a media tool, read at call time.

    The variables are FFMPEG and FFPROBE, matching
    tools/translate-tv-subtitles-ja.py. Call-time reads let tests set the
    variable; ``os.environ`` stays in this config module per the
    boundary-adapter rule.

    :param name: Tool name, e.g. "ffmpeg" (uppercased to find the variable).
    :returns: The override value, or None when unset or empty.
    """
    return os.environ.get(name.upper()) or None


def tool_search_path() -> str:
    """Return the PATH media tools are resolved from, read at call time.

    :returns: The current PATH, or "" when unset.
    """
    return os.environ.get("PATH", "")


# The app launcher supplies MEDIASERVER_ROOT from the selected profile before
# importing pipeline modules. Read it once at import time. Standalone use falls
# back to the checkout-relative apps root derived from this module's location.
ROOT = Path(os.environ.get("MEDIASERVER_ROOT", str(Path(__file__).resolve().parents[2])))
# Independent override for the media root, deliberately separate from ROOT.
# The media library can live on a different volume; DOWNLOADS_ROOT,
# QUARANTINE_ROOT and LOCK_FILE below stay anchored to ROOT. Without an
# explicit media-root override, the media library is ROOT / "media".
MEDIA_ROOT = _env_absolute_path_override("MEDIASERVER_MEDIA_ROOT", ROOT / "media")
DOWNLOADS_ROOT = ROOT / "downloads"
MOVIES_ROOT = MEDIA_ROOT / "Movies"
TV_ROOT = MEDIA_ROOT / "TV"
QUARANTINE_ROOT = ROOT / "quarantine"
# flock(2) file guarding against concurrent organizer runs (see lib/run_lock.py).
LOCK_FILE = ROOT / "logs" / "organize-media.pylock"

VIDEO_EXTS = {".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".mpg", ".mpeg"}
INCOMPLETE_MARKERS = (".part", ".partial", ".tmp", ".aria2", ".!qb", ".crdownload", ".download")

# Match common extras/trailers/featurettes naming
EXTRA_KEYWORDS = ("trailer", "tv-spot", "tv spot", "featurette", "extra")

# Known torrent release group tags -- files whose guessit title is only one of
# these are watermark/sample files, not real movies. Quarantine them instead of
# adding them to the library.
RELEASE_GROUP_TITLES = {
    "etrg", "yify", "yts", "rarbg", "fgt", "ettv", "cmrg", "ion10", "tigole",
    "sparks", "rovers", "fum", "hdr", "framestor",
}

# Every extension the server indexes as an external subtitle
# (Emby.Naming NamingOptions.SubtitleFileExtensions), plus ".idx", which is not
# indexed itself but is the half of a VobSub pair without which the ".sub" is
# undecodable. This is the single source of truth: a tool that keeps its own
# copy drifts, and an extension the pipeline PRODUCES but a copy omits is a
# subtitle that gets silently skipped or left behind. ".sup" is produced by
# tools/lib/transcode.py when it extracts a bitmap track to a sidecar.
SUBTITLE_EXTS = {".ass", ".idx", ".mks", ".sami", ".smi", ".srt", ".ssa",
                 ".sub", ".sup", ".vtt"}
ARCHIVE_EXTS = {".zip", ".rar"}
JUNK_EXTS = {".txt", ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".website", ".url", ".exe", ".lnk"}

# The streaming encode target's largest frame. Level 4.0 cannot hold 2160p, so
# anything bigger is scaled to fit (aspect preserved, never upscaled); see
# lib/transcode_video_scaling.py.
TRANSCODE_MAX_WIDTH = 1920
TRANSCODE_MAX_HEIGHT = 1080
# ffprobe color_transfer values that mean HDR (PQ, HLG). An 8-bit BT.709
# target cannot carry them, so such sources are tone-mapped to SDR.
HDR_COLOR_TRANSFERS = {"smpte2084", "arib-std-b67"}

_DIRECT_PLAY_VIDEO_CODECS = {"h264"}
_DIRECT_PLAY_AUDIO_CODECS = {"aac", "mp3", "ac3", "eac3"}
# A codec-name match alone does not mean a browser can decode a specific h264
# stream: jellyfin-web-codex/src/scripts/browserDeviceProfile.js gates real
# playback on profile ("high 10" is excluded for Safari/Edge/iOS/webOS/Tizen/
# mobile, and only conditionally added elsewhere), 8-bit pix_fmt, and level
# (maxH264Level defaults to 42, i.e. 4.2, for every browser unconditionally).
# Our own encode target (transcode.py::_video_encode_args) always produces
# high@4.0 8-bit, comfortably inside this envelope; these exist to catch a
# pre-existing library file that is merely NAMED h264 but falls outside it.
_DIRECT_PLAY_VIDEO_PROFILES = {"high", "main", "baseline", "constrained baseline"}
_DIRECT_PLAY_VIDEO_PIX_FMTS = {"yuv420p"}
_MAX_DIRECT_PLAY_VIDEO_LEVEL = 42
# Audio codecs every browser (Chrome, Firefox, Safari, Edge) can decode with no
# server-side transcode. AC3/E-AC3/DTS/TrueHD are NOT here even though
# _DIRECT_PLAY_AUDIO_CODECS above treats ac3/eac3 as "copy this track
# unchanged" -- that set is about which codecs the pipeline preserves
# losslessly, not about which ones a browser can actually play.
BROWSER_AUDIO_CODECS = {"aac", "mp3", "opus", "vorbis", "flac"}
# The Panasonic TX-LF37E30 (the USB-portability target, see
# deliverables/tv-format-matrix.md) is a second, separate audio consumer with
# its own limits, and they are NARROWER than the browser's. Round 2 of the USB
# clip test (2026-09-05, clips 15/16/17/25/26/27/28/32, owner-observed on the
# set) measured:
#
#     aac  48000 Hz  1ch   plays        (clip 16)
#     aac  48000 Hz  2ch   plays        (clip 17)
#     aac  48000 Hz  6ch   plays        (clip 15)
#     ac3  48000 Hz  2ch   plays        (clip 28)
#     eac3 48000 Hz  2ch   SILENT, on-screen "audio format not supported" (27)
#     eac3 48000 Hz  6ch   SILENT, same message, in MKV (26) and MP4 (25)
#     aac  96000 Hz  2ch   SILENT       (clip 32)
#     mp3  48000 Hz  2ch   SILENT       (round 1, clip 06)
#
# So channel count is NOT a variable (round 1's silent AAC 5.1 clip was the
# default-disposition confound, not a channel limit), sample rate IS one, and
# E-AC3 does not play at all -- which matters because 124 library files use it
# and the previous rule listed it as playable at any channel count, hiding
# every one of them from TV remediation.
#
# Kept apart from BROWSER_AUDIO_CODECS on purpose: MP3 and AAC 96k are both
# browser-playable, so folding these together would keep hiding TV-silent
# files the browser criterion alone already misses.
#
# Unobserved combinations must fail CLOSED. Being wrong towards "playable"
# leaves a file silent on the television with nothing flagging it; being wrong
# the other way costs one added AAC track. Hence 44100 is absent: common, but
# never put in front of this TV.
TV_AUDIO_CODECS = {"aac", "ac3"}
TV_AUDIO_SAMPLE_RATES = {48000}
# Containers shown to carry MULTICHANNEL AAC to this TV's decoder. Clips 02 and
# 15 are byte-for-byte equivalent streams -- aac LC, 48000 Hz, 6ch, 5.1 layout,
# video default=1 -- differing only in container, and 02 (MP4) was silent while
# 15 (MKV) played. AC3 is not container-sensitive (clip 03 is AC3 5.1 in MP4 and
# played), so this applies to AAC alone. Anything not listed gets the MP4
# answer, which is the conservative one.
TV_MULTICHANNEL_AAC_CONTAINERS = {".mkv"}
# Audio codecs ffmpeg's MP4 muxer accepts as a plain "-c:a copy", verified by
# direct probe on this host 2026-09-05 (encode a 1s tone, remux into .mp4, no
# -strict flag on the copy): aac/mp3/ac3/eac3/alac/opus/dts/flac/pcm_s16le/
# pcm_s24le all wrote a header; truehd and mlp were refused with "truehd in MP4
# support is experimental, add '-strict -2'". The container-only pass copies
# every source audio stream, so a stream outside this set would fail the whole
# remux at output-open.
#
# Deliberately an ALLOWLIST, not a truehd/mlp denylist: an unlisted codec makes
# the container-only pass skip the file and leave it to the full pass, which
# re-encodes the audio properly. That fails closed (the file waits) instead of
# failing open (the remux dies, or a lossless track is silently dropped from a
# file whose source .mkv is then retired). pcm_bluray/pcm_dvd/vorbis are absent
# because this ffmpeg build has no encoder for them, so they could not be
# probed -- untested means excluded, not assumed.
_MP4_COPYABLE_AUDIO_CODECS = {"aac", "mp3", "ac3", "eac3", "alac", "opus",
                              "dts", "flac", "pcm_s16le", "pcm_s24le"}
_REMUX_FROM_EXTS = {".avi", ".wmv", ".mpg", ".mpeg", ".mov"}

JELLYFIN_URL = os.environ.get("JELLYFIN_URL")
JELLYFIN_TOKEN = os.environ.get("JELLYFIN_TOKEN")
UPDATES_FILE = os.environ.get("ORGANIZE_MEDIA_UPDATES_FILE")
# Resolve new TV releases through local/remote provider IDs before falling
# back to title-only folder matching. Remote misses/failures and successful
# identities are cached so episode packs do not repeat metadata requests.
SHOW_METADATA_LOOKUP = (
    os.environ.get("ORGANIZE_MEDIA_SHOW_METADATA_LOOKUP", "1").strip().lower()
    in {"1", "true", "yes"}
)
SHOW_IDENTITY_CACHE = ROOT / "cache" / "show-identities.json"
SUBTITLECAT_PREFETCH = os.environ.get("SUBTITLECAT_PREFETCH", "").strip().lower() in {"1", "true", "yes"}
SUBTITLECAT_PREFETCH_LANGS = os.environ.get("SUBTITLECAT_PREFETCH_LANGS", "ja")
SUBTITLECAT_PREFETCH_QUERY = os.environ.get("SUBTITLECAT_PREFETCH_QUERY")
MIN_STABLE_AGE_SECONDS = int(os.environ.get("ORGANIZE_MEDIA_MIN_AGE_SECONDS", "120"))
# Optional per-run completion cap. Zero preserves the historical unlimited
# behavior; the organizer wrapper sets a small cap so a bounded cron pass
# cannot grow beyond its outer timeout when many packs are ready.
MAX_ITEMS_PER_RUN = int(os.environ.get("ORGANIZE_MEDIA_MAX_ITEMS_PER_RUN", "0"))
# Files smaller than this are almost certainly not real movies (watermarks, samples, etc.)
MIN_MOVIE_SIZE_MB = int(os.environ.get("ORGANIZE_MEDIA_MIN_MOVIE_SIZE_MB", "50"))
MIN_MOVIE_SIZE_BYTES = MIN_MOVIE_SIZE_MB * 1024 * 1024
# Media shorter than this (seconds) is not a real movie even if the file is large
MIN_MOVIE_DURATION_SECONDS = int(os.environ.get("ORGANIZE_MEDIA_MIN_MOVIE_DURATION_SECONDS", "240"))  # 4 minutes
EXTRACT_ENGLISH_SRT = os.environ.get("ORGANIZE_MEDIA_EXTRACT_ENGLISH_SRT", "1").strip().lower() in {"1", "true", "yes"}
NORMALIZE_AUDIO = os.environ.get("ORGANIZE_MEDIA_NORMALIZE_AUDIO", "1").strip().lower() in {"1", "true", "yes"}
NORMALIZE_AUDIO_THRESHOLD_DB = float(os.environ.get("ORGANIZE_MEDIA_NORMALIZE_THRESHOLD", "-28"))
# Cheap copy-only remux pass: faststart MP4s, push legacy containers into MKV.
REMUX_FOR_STREAMING = os.environ.get("ORGANIZE_MEDIA_REMUX_FOR_STREAMING", "1").strip().lower() in {"1", "true", "yes"}
# Heavy re-encode pass: convert non-direct-play codecs to H.264 + AAC so the
# server can avoid on-the-fly transcoding (which freezes on low-RAM hardware).
TRANSCODE_FOR_STREAMING = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_FOR_STREAMING", "1").strip().lower() in {"1", "true", "yes"}
TRANSCODE_PRESET = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_PRESET", "medium")
TRANSCODE_CRF = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_CRF", "19")
# Nice value for the heavy transcode pass.  19 = lowest priority on macOS/BSD,
# so playback, scans, and other work always preempt it for *CPU*.  Note: nice does
# NOT throttle disk I/O -- see _priority_prefix(), which adds macOS background QoS
# (taskpolicy -b) so the encode's heavy reads/writes also yield to streaming.
TRANSCODE_NICE = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_NICE", "19")
# Force a keyframe at least every N seconds when re-encoding video.  Jellyfin's web
# client cannot direct-play MKV, so it remuxes to HLS with the video stream *copied*
# and can only cut segments on existing keyframes.  Its segment target is 6 s
# (-hls_time 6); a source with the libx264 default ~10 s GOP forces Jellyfin to emit
# irregular segments, and the web player then restarts the transcode every few
# seconds chasing a boundary that never lands on a keyframe -- the dominant cause of
# playback "freezing".  Keep this an even divisor of 6 (2 or 3) so every HLS segment
# boundary coincides with a keyframe.
TRANSCODE_KEYINT_SECONDS = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_KEYINT_SECONDS", "2")
# A file whose video is already a direct-play codec is re-encoded anyway -- purely to
# shorten its GOP -- when its keyframes are spaced wider than this many seconds.
# 6.0 == Jellyfin's hls_time, the point past which segment alignment breaks.
MAX_KEYFRAME_GAP_SECONDS = float(os.environ.get("ORGANIZE_MEDIA_MAX_KEYFRAME_GAP_SECONDS", "6.0"))
# When set, the heavy pass does container work ONLY: a file whose video and
# audio codecs already direct-play is remuxed into MP4 (+faststart, text subs
# to mov_text) with the streams copied, and a long GOP alone does NOT trigger a
# re-encode. Files needing a real codec change are left for the normal pass.
#
# This exists to separate "make it playable now" from "make it perfect", which
# on this host are hours and weeks apart: measured 2026-09-03, 9 of 10 sampled
# h264 library files have a ~10s GOP and so need a full re-encode at roughly
# 30 minutes each, about 12 days of continuous encoding for the library, while
# the copy-only remux that actually fixes playback startup takes ~1 minute.
# A file remuxed by this pass still reports as needing work afterwards, so the
# scheduled pass re-visits it and finishes the job.
CONTAINER_ONLY_PASS = os.environ.get(
    "ORGANIZE_MEDIA_CONTAINER_ONLY_PASS", "").strip().lower() in {"1", "true", "yes"}
# Floor for the source-capped re-encode bitrate. A re-encode never spends more
# than ~1.3x what the source carries (see _source_capped_hw_bitrate), but a
# very low-bitrate source must not drag the target down to something visibly
# worse than what it started from.
MIN_REENCODE_BITRATE_BPS = int(os.environ.get("ORGANIZE_MEDIA_MIN_REENCODE_BITRATE", "800000"))
# Use Apple VideoToolbox hardware H.264 when available.  On this slow Mac host x264
# medium runs at ~0.2x real-time (a 90-min film ~= 5 h); hardware encoding plus GPU
# decode runs ~2x real-time, turning a weeks-long library pass into days.  Quality per
# bitrate is lower than x264, so hardware uses a higher, resolution-scaled bitrate
# (TRANSCODE_HW_BITRATES below).  Set to 0 to force software x264 (e.g. on a fast host
# or a machine without VideoToolbox); falls back to x264 automatically if the encoder
# is missing.
TRANSCODE_HW = os.environ.get("ORGANIZE_MEDIA_TRANSCODE_HW", "1").strip().lower() in {"1", "true", "yes"}
# VideoToolbox target bitrate by source height (px ceiling -> bitrate).  Picked tallest
# bucket the source's height fits in.  Higher than the equivalent x264 CRF stream
# because VT is less efficient; storage is the cheap side of this tradeoff.
TRANSCODE_HW_BITRATES = [(720, "5M"), (1080, "8M"), (1440, "12M"), (2160, "16M")]
# Background media work always yields by default, including between sessions.
# Opt-in adaptive mode remains available for isolated non-production callers;
# scheduled jobs explicitly pin this off so a quiet attendance poll cannot
# release an encode while a connected viewer is about to start or seek.
ADAPTIVE_ENCODE_THROTTLE = os.environ.get(
    "ORGANIZE_MEDIA_ADAPTIVE_THROTTLE", "0").strip().lower() in {"1", "true", "yes"}
#: Seconds between playback checks while an encode runs.
ADAPTIVE_THROTTLE_POLL_SECONDS = int(
    os.environ.get("ORGANIZE_MEDIA_ADAPTIVE_THROTTLE_POLL", "10"))
#: Seconds of keyboard/mouse silence before the clamp may be lifted.  Playback is
#: not the only thing an encode can slow down: the owner working at the machine
#: counts too, so the clamp only comes off when nothing at all wants the host.
ADAPTIVE_THROTTLE_MIN_IDLE_SECONDS = int(
    os.environ.get("ORGANIZE_MEDIA_ADAPTIVE_THROTTLE_MIN_IDLE", "300"))
#: Seconds an encode may write NOTHING to its output before it is killed as
#: stuck.  This replaces the wall-clock timeout as the real liveness check: the
#: same stream copy was measured at ~23 MB/s unclamped on an idle host and at
#: 256 KB/s clamped with a second job on the disk, so no constant deadline can
#: tell "slow" from "hung".  Output growth can.  Generous because ffmpeg writes
#: in large buffered chunks and a contended disk can be quiet for a while.
ENCODE_STALL_SECONDS = float(
    os.environ.get("ORGANIZE_MEDIA_ENCODE_STALL_SECONDS", "900"))
#: Seconds between the watchdog's "encode running, N bytes written" log lines,
#: so an encode that never finishes leaves its throughput in the log.
ENCODE_HEARTBEAT_SECONDS = 600.0
#: Seconds between output-size checks for the stall watchdog.
ENCODE_STALL_POLL_SECONDS = float(
    os.environ.get("ORGANIZE_MEDIA_ENCODE_STALL_POLL", "30"))
#: Worst sustained throughput measured for a whole-file stream-copy encode
#: (map 0 -c copy plus one or two appended AAC tracks: fix-tv-audio-catchup.py
#: and audio_reorder.py's shape) on this host, bytes/sec, ACROSS 14 IDLE OR
#: NORMALLY-LOADED runs. Feeds encode_watchdog.stream_copy_outer_timeout_seconds,
#: which used to treat "2x this" as the kill bound -- t_260920_052208_901
#: found that bound SIGKILLs honest work under Darwin's background I/O tier
#: (tools/backfill-streaming.py's _self_throttle_disk_io, inherited by every
#: forked ffmpeg/ffprobe), which starves throughput by a factor with no
#: ceiling rather than slowing it proportionally: a live control measured
#: the SAME tiny ffprobe call at 0.372s at normal priority and still running
#: at 2m23s throttled, with a second run never finishing a 400s budget. No
#: throughput sample -- this one, the 2026-09-20 181,656 B/s "Old Guard 2"
#: measurement, or any other single reading -- can be multiplied into a
#: number safe against an unbounded starvation factor, so this constant is
#: deliberately NOT recalibrated from that measurement (see
#: stream_copy_outer_timeout_seconds' docstring for the actual fix: compose
#: with EncodeStallWatchdog's own cached growth signal, and use this
#: constant only for a much wider backstop ceiling behind that). Computed
#: 2026-09-19 from the 14 real "fixed" runs in
#: deliverables/tv-audio-catchup/tv-audio-catchup-results.jsonl, dividing
#: each file's CURRENT on-disk size by its recorded seconds:
#:   768,702.6 B/s  Pitch Perfect 3 (2017)          7,876,126,871 B / 10246.0s
#: 1,801,411.5 B/s  Liar Liar (1997)                5,166,087,884 B /  2867.8s
#: 2,059,544.3 B/s  Oui Mais                        3,394,129,047 B /  1648.0s
#: 2,381,804.3 B/s  The Marching Band (2024)        6,619,034,098 B /  2779.0s
#: 2,402,478.6 B/s  Save The Last Dance (2001)      7,092,597,210 B /  2952.2s
#: 2,547,329.7 B/s  The Matrix Resurrections (2021) 9,492,624,152 B /  3726.5s
#: 2,562,596.9 B/s  Legally Blonde 2 (2003)         6,059,772,820 B /  2364.7s
#: 2,997,788.5 B/s  The Breakfast Club (1985)       6,106,195,365 B /  2036.9s
#: 3,176,294.1 B/s  L Auberge Espagnole (2002)      7,912,783,903 B /  2491.2s
#: 3,809,099.8 B/s  Incendies (2010)                2,811,877,493 B /   738.2s
#: 4,125,325.0 B/s  The Matrix (1999)               8,450,728,330 B /  2048.5s
#: 8,295,608.4 B/s  River (2023)                    5,373,895,093 B /   647.8s
#: 8,400,269.2 B/s  Driving Madeleine (2022)        5,782,745,349 B /   688.4s
#: 14,976,599.9 B/s Cunk on Life (2024)              4,500,468,261 B /   300.5s
#: A ~19x spread on production hardware, all under real (sometimes
#: contended) conditions -- the same shape of spread ENCODE_STALL_SECONDS'
#: own docstring cites for why a flat deadline is unsound. This constant is
#: deliberately the WORST of the 14 measured runs, not an average or median:
#: the outer bound exists to survive the slow-but-honest case, the same way
#: the 1800s constant it replaces did not.
STREAM_COPY_MIN_BYTES_PER_SECOND = (
    float(os.environ["ORGANIZE_MEDIA_STREAM_COPY_MIN_BPS"])
    if os.environ.get("ORGANIZE_MEDIA_STREAM_COPY_MIN_BPS")
    else 768_702.0)
#: Sustained h264_videotoolbox throughput on this host with the background
#: clamp OFF, bytes/sec. Measured 2026-09-09 (t_260906_145134_255) running the
#: pipeline's own hardware command (-c:v h264_videotoolbox -b:v 8M, the real
#: 1080p bucket) against a real library file (Moon (2009), two non-overlapping
#: 300s segments), output growth differenced over interleaved 45s
#: unclamped/clamped windows so a slowly-varying confound hits both sides
#: equally: 717,297 B/s unclamped (two trials: 646,623 and 786,432) vs
#: 611,670 B/s clamped (two trials: 553,415 and 669,924) -- ratio ~1.17x. Both
#: figures are well below the ~256 KB/s-vs-implied-970-KB/s (~3.8x) spread
#: this module's own docstring records, because the host was NOT quiet during
#: this measurement: a stuck-looking copy-only remux (see
#: logs/backfill-streaming.log 2026-09-09 PID 41380, a faststart tail that sat
#: at a fixed size for 30+ minutes) and two unrelated multi-hour batch scripts
#: (migrate-retired-originals.py, fix-tv-audio-catchup.py) held the same
#: volume throughout. The interleaving held the unclamped/clamped RATIO
#: consistent across both trials despite absolute levels drifting 25%+
#: between them, which is why the ratio is trusted here even though a
#: genuinely idle-host figure (expected higher, per the historical 3.8-5.7x
#: spread) was not obtainable in this session -- see
#: deliverables/t_260906_145134_255/hardware-encode-rate-measurement.md.
#: Feeds the hardware-restart decision: restart only when the CURRENT clamped
#: run's remaining time (measured live from output growth) would exceed a
#: full unclamped rerun at this rate plus HARDWARE_RESTART_SAFETY_MARGIN_SECONDS.
HARDWARE_ENCODE_UNCLAMPED_BYTES_PER_SECOND = (
    float(os.environ["ORGANIZE_MEDIA_HARDWARE_UNCLAMPED_BPS"])
    if os.environ.get("ORGANIZE_MEDIA_HARDWARE_UNCLAMPED_BPS")
    else 717_297.0)
#: Fixed buffer added to the unclamped full-rerun estimate before comparing
#: it to the clamped remaining-time estimate. Absorbs process-relaunch
#: overhead (SIGKILL, exec, VideoToolbox session negotiation) and the
#: encode_phase_observer video-bitrate-only size estimate's approximation
#: error (it ignores audio/container overhead).
HARDWARE_RESTART_SAFETY_MARGIN_SECONDS = float(
    os.environ.get("ORGANIZE_MEDIA_HARDWARE_RESTART_MARGIN_SECONDS", "300"))
# Run ffsubsync against every .srt sidecar to re-align timing using audio VAD.
SYNC_SUBTITLES = os.environ.get("ORGANIZE_MEDIA_SYNC_SUBTITLES", "1").strip().lower() in {"1", "true", "yes"}
# Cache of each video's own extracted embedded-English-track cue starts
# ("{video.stem}.s{index}.embedded.srt"), used as the deterministic timing
# gate's reference (tools/lib/subtitle_timing_reference.py). Shared across
# every sidecar of the same video, and across repeated pipeline runs.
SUBTITLE_TIMING_CACHE_DIR = ROOT / "cache" / "subtitle-timing-embedded-en"
# Hash-matched subtitle prefetch: queries OpenSubtitles for any sub whose file
# hash matches this exact encode, in any of the configured languages.  Hash
# matches are guaranteed to be in sync, so we don't need ffsubsync on them.
PREFETCH_HASH_SUBS = os.environ.get("ORGANIZE_MEDIA_PREFETCH_HASH_SUBS", "1").strip().lower() in {"1", "true", "yes"}
PREFETCH_HASH_SUB_LANGS = os.environ.get("ORGANIZE_MEDIA_PREFETCH_HASH_SUB_LANGS",
                                        "en,fr,es,de,ja,it,pt-BR,pl,nl,sv")
# Jimaku is a Japanese-subtitle catalogue and catches episode releases that
# general-purpose providers cannot identify by file hash.
PREFETCH_JIMAKU_SUBS = os.environ.get("ORGANIZE_MEDIA_PREFETCH_JIMAKU_SUBS", "1").strip().lower() in {"1", "true", "yes"}
# Cap how far ffsubsync is allowed to shift a subtitle.  90s comfortably covers
# real-world misalignments (intro/credit padding, encoder delay) but rejects
# the dominant failure mode where the .srt is from a different cut/release --
# in those cases ffsubsync will pick the best alignment within its window,
# which is almost always wrong.  Default ffsubsync value is 60s.
SUBSYNC_MAX_OFFSET = os.environ.get("ORGANIZE_MEDIA_SUBSYNC_MAX_OFFSET", "90")
