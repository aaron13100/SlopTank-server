# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Video compatibility and encoder policy for the streaming transcode pass."""

from __future__ import annotations

import logging
from pathlib import Path

from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.media_probe import _max_keyframe_gap
from lib.organize_media_config import (
    MAX_KEYFRAME_GAP_SECONDS,
    MIN_REENCODE_BITRATE_BPS,
    TRANSCODE_CRF,
    TRANSCODE_HW,
    TRANSCODE_HW_BITRATES,
    TRANSCODE_KEYINT_SECONDS,
    TRANSCODE_PRESET,
    _DIRECT_PLAY_AUDIO_CODECS,
    _DIRECT_PLAY_VIDEO_CODECS,
    _DIRECT_PLAY_VIDEO_PIX_FMTS,
    _DIRECT_PLAY_VIDEO_PROFILES,
    _MAX_DIRECT_PLAY_VIDEO_LEVEL,
)
from lib.transcode_video_scaling import VideoScalingPlan
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")


def _has_videotoolbox() -> bool:
    """Return whether this ffmpeg build lists Apple's H.264 encoder.

    :raises MediaToolMissingError: ffmpeg cannot be resolved. A missing
        binary must not read as "no hardware encoder": there is no software
        encoder either, so selecting one would only defer the failure.
    """
    cached = getattr(_has_videotoolbox, "_cache", None)
    if cached is not None:
        return cached
    ffmpeg = require_tool("ffmpeg")
    try:
        output = run_cancellable_capture(
            [ffmpeg, "-hide_banner", "-encoders"],
            timeout=15,
        )
        result = "h264_videotoolbox" in (output.stdout or "")
    except EncodeCancelled:
        raise
    except Exception:
        # Probe failure safely selects the software encoder.
        result = False
    _has_videotoolbox._cache = result
    return result


def _hw_bitrate_for_height(height: int | None) -> str:
    """Return the configured VideoToolbox bitrate bucket."""
    source_height = height or 1080
    for ceiling, bitrate in TRANSCODE_HW_BITRATES:
        if source_height <= ceiling:
            return bitrate
    return TRANSCODE_HW_BITRATES[-1][1]


def _source_capped_hw_bitrate(height: int | None, info: dict | None) -> str:
    """Return the resolution bitrate, capped at 1.3x known source video."""
    ladder = _hw_bitrate_for_height(height)
    if not info:
        return ladder
    source_bps = info.get("video_bit_rate")
    if not source_bps:
        total_bps = info.get("total_bit_rate")
        audio_bps = info.get("audio_bit_rate_total") or 0
        source_bps = max((total_bps or 0) - audio_bps, 0)
    if not source_bps:
        return ladder
    multiplier = 1_000_000 if ladder.endswith("M") else 1_000
    ladder_bps = int(ladder.rstrip("Mk")) * multiplier
    capped = max(int(source_bps * 1.3), MIN_REENCODE_BITRATE_BPS)
    if capped >= ladder_bps:
        return ladder
    return f"{capped // 1000}k"


def _resolve_use_hardware(use_hardware: bool | None) -> bool:
    """Resolve the encoder choice: explicit, else config plus availability."""
    if use_hardware is None:
        return TRANSCODE_HW and _has_videotoolbox()
    return use_hardware


def _video_encode_args(height: int | None, info: dict | None = None,
                       use_hardware: bool | None = None,
                       scaling: VideoScalingPlan | None = None) -> list[str]:
    """Build the video encoder portion of the ffmpeg argument vector.

    :param height: Source height; the bitrate bucket follows the OUTPUT
        height instead when ``scaling`` knows it.
    :param scaling: The size/HDR conversion plan whose output colour tags
        are appended and whose hardware frames must not be re-formatted.
    """
    keyframes = [
        "-force_key_frames", f"expr:gte(t,n_forced*{TRANSCODE_KEYINT_SECONDS})",
    ]
    use_hardware = _resolve_use_hardware(use_hardware)
    scaling = scaling or VideoScalingPlan()
    # Explicit -colorspace/-color_* flags on VideoToolbox frames make ffmpeg
    # reinitialise the filter graph and fail (-78, measured 2026-09-30);
    # scale_vt already stamps bt709 on those frames, so only the software
    # encoder, which receives downloaded frames, is told the tags.
    tags = list(scaling.color_tags)
    if use_hardware:
        pix_fmt = [] if scaling.hardware_frames else ["-pix_fmt", "yuv420p"]
        return [
            "-c:v", "h264_videotoolbox",
            "-b:v", _source_capped_hw_bitrate(
                scaling.output_height or height, info),
            "-profile:v", "high",
        ] + pix_fmt + keyframes
    return [
        "-c:v", "libx264", "-preset", TRANSCODE_PRESET,
        "-crf", TRANSCODE_CRF, "-pix_fmt", "yuv420p",
        "-profile:v", "high", "-level", "4.0",
    ] + tags + keyframes


def _video_requires_reencode_except_gop(info: dict) -> bool:
    """Return whether codec/profile/pixel format/level requires re-encode."""
    video_codec = info.get("video_codec") or ""
    needs_video = bool(video_codec) and video_codec not in _DIRECT_PLAY_VIDEO_CODECS
    if video_codec == "h264" and not needs_video:
        profile = (info.get("video_profile") or "").strip().lower()
        pixel_format = info.get("video_pix_fmt") or ""
        level = info.get("video_level")
        if profile and profile not in _DIRECT_PLAY_VIDEO_PROFILES:
            needs_video = True
        elif pixel_format and pixel_format not in _DIRECT_PLAY_VIDEO_PIX_FMTS:
            needs_video = True
        elif isinstance(level, int) and level > _MAX_DIRECT_PLAY_VIDEO_LEVEL:
            needs_video = True
    return needs_video


def _needs_reencode(path: Path, info: dict,
                    target_suffix: str = ".mp4") -> tuple[bool, bool, bool]:
    """Decide which video, audio, and container dimensions need work."""
    video_codec = info.get("video_codec") or ""
    audio_codec = info.get("audio_codec") or ""
    sample_rate = info.get("audio_sample_rate") or 0
    suffix = path.suffix.lower()
    needs_video = _video_requires_reencode_except_gop(info)
    needs_audio = (
        (bool(audio_codec) and audio_codec not in _DIRECT_PLAY_AUDIO_CODECS)
        or sample_rate > 48000
    )
    if bool(video_codec) and not needs_video:
        gap = _max_keyframe_gap(path)
        if gap is not None and gap > MAX_KEYFRAME_GAP_SECONDS:
            log.info(
                "long GOP: %s keyframes up to %.1fs apart (> %.1fs) -> "
                "re-encoding video", path.name, gap, MAX_KEYFRAME_GAP_SECONDS,
            )
            needs_video = True
    if target_suffix == ".mkv":
        needs_container = suffix != ".mkv"
    else:
        needs_container = suffix not in (".mp4", ".m4v")
    return needs_video, needs_audio, needs_container
