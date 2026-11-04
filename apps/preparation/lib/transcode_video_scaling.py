# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Streaming-target geometry and HDR-to-SDR planning for the video encode.

The streaming target is H.264 High@4.0, 8-bit yuv420p, at most 1920x1080
(see ``lib.organize_media_config``). Two source properties break that target
unless the encode converts them:

* **Size.** Level 4.0 cannot hold 2160p. A source wider than 1920 or taller
  than 1080 is scaled to fit that box, aspect preserved, even dimensions; a
  source inside the box is never upscaled.
* **HDR.** An 8-bit stream cannot carry PQ or HLG, so an HDR source encoded
  with only a pixel-format change comes out washed out. HDR sources are
  tone-mapped to SDR BT.709 and the output is tagged bt709.

Tone-mapping uses VideoToolbox: ``-hwaccel videotoolbox`` decode, then
``scale_vt`` with bt709 matrix/primaries/transfer targets. This ffmpeg build
has no zscale and no libplacebo, so there is no software tone-mapper to fall
back to; when ``scale_vt`` is missing an HDR source fails loudly rather than
shipping washed-out colour. The hardware path also replaces a software 10-bit
HEVC decode, which is what made every 2160p conversion unfinishable.

Measured facts (2026-09-30, ffmpeg 8.0.1, macOS 12.7.6): ``scale_vt`` with
all three colour options and no ``-pix_fmt`` yields a bt709-tagged yuv420p
1080p stream; ``-pix_fmt yuv420p`` after ``videotoolbox_vld`` frames fails
(-78 "Function not implemented") so the hardware encoder must receive the
frames untouched; the software encoder needs ``hwdownload,format=p010le``
(``scale_vt`` emits 10-bit; ``nv12`` is refused by ``hwdownload``).

Limitation: a Dolby Vision profile 5 source has no HDR10/HLG-compatible base
layer and would still tone-map incorrectly. The ffprobe output for the
library's DV.HDR WEB-DLs exposes no DV side data, so it is not detected here.
"""

from __future__ import annotations

import subprocess
from dataclasses import dataclass

from lib.encode_cancellation import run_cancellable_capture
from lib.organize_media_config import (
    HDR_COLOR_TRANSFERS,
    TRANSCODE_MAX_HEIGHT,
    TRANSCODE_MAX_WIDTH,
)
from lib.transcode_types import TranscodeFailedError
from lib.media_tools import require_tool

_HWACCEL_INPUT_ARGS = (
    "-hwaccel", "videotoolbox", "-hwaccel_output_format", "videotoolbox_vld",
)
_BT709_TAGS = (
    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
)
_BT709_TARGET = "color_matrix=bt709:color_primaries=bt709:color_transfer=bt709"
_SOFTWARE_DOWNLOAD = "hwdownload,format=p010le,format=yuv420p"


@dataclass(frozen=True)
class VideoScalingPlan:
    """Extra ffmpeg arguments one planned video encode needs.

    :param input_args: Options that must precede ``-i`` (hardware decode).
    :param video_filter: The ``-vf`` chain, or None for no filtering.
    :param output_height: Height of the encoded output, for the bitrate
        bucket; None when the source height is unknown.
    :param color_tags: Output colour tags (bt709 for tone-mapped output).
    :param hardware_frames: True when the encoder receives VideoToolbox
        frames, so ``-pix_fmt`` must not be passed.
    """

    input_args: tuple[str, ...] = ()
    video_filter: str | None = None
    output_height: int | None = None
    color_tags: tuple[str, ...] = ()
    hardware_frames: bool = False


def is_hdr_source(info: dict) -> bool:
    """Return whether the probed video uses a PQ or HLG transfer."""
    return (info.get("video_color_transfer") or "") in HDR_COLOR_TRANSFERS


def fit_within_output_box(width: int, height: int) -> tuple[int, int] | None:
    """Return the even-dimension size that fits 1920x1080, or None.

    :param width: Source width in pixels.
    :param height: Source height in pixels.
    :returns: ``(width, height)`` of the scaled output, or None when the
        source already fits and must not be touched (never upscaled).
    """
    if width <= TRANSCODE_MAX_WIDTH and height <= TRANSCODE_MAX_HEIGHT:
        return None
    factor = min(TRANSCODE_MAX_WIDTH / width, TRANSCODE_MAX_HEIGHT / height)
    return _even(width * factor), _even(height * factor)


def _even(value: float) -> int:
    return max(2, int(round(value / 2.0)) * 2)


def _has_scale_vt() -> bool:
    """Return whether this ffmpeg build lists the ``scale_vt`` filter.

    :raises MediaToolMissingError: ffmpeg cannot be resolved. A missing
        binary must not read as "no filter": tone-mapping cannot be
        promised and no encode can run, so selecting a fallback would only
        defer the failure.
    """
    cached = getattr(_has_scale_vt, "_cache", None)
    if cached is not None:
        return cached
    ffmpeg = require_tool("ffmpeg")
    try:
        output = run_cancellable_capture(
            [ffmpeg, "-hide_banner", "-filters"],
            timeout=15,
        )
        result = " scale_vt " in (output.stdout or "")
    except (OSError, subprocess.SubprocessError):
        # Probe failure means tone-mapping cannot be promised, so HDR
        # sources fail loudly below instead of encoding washed out.
        result = False
    _has_scale_vt._cache = result
    return result


def _dimensions(info: dict) -> tuple[int | None, int | None]:
    return info.get("video_width") or None, info.get("video_height") or None


def _plan_hdr(info: dict, *, use_hardware: bool,
              scale_vt_available: bool | None) -> VideoScalingPlan:
    width, height = _dimensions(info)
    if not width or not height:
        raise TranscodeFailedError(
            "cannot tone-map an HDR source without its probed dimensions "
            f"(width={width}, height={height})")
    if scale_vt_available is None:
        scale_vt_available = _has_scale_vt()
    if not scale_vt_available:
        raise TranscodeFailedError(
            "HDR source needs ffmpeg's scale_vt filter to tone-map to SDR "
            "BT.709 and this ffmpeg build lacks it; refusing to encode "
            "washed-out colour")
    out_width, out_height = fit_within_output_box(width, height) or (
        _even(width), _even(height))
    chain = f"scale_vt=w={out_width}:h={out_height}:{_BT709_TARGET}"
    if not use_hardware:
        chain += "," + _SOFTWARE_DOWNLOAD
    return VideoScalingPlan(
        input_args=_HWACCEL_INPUT_ARGS, video_filter=chain,
        output_height=out_height, color_tags=_BT709_TAGS,
        hardware_frames=use_hardware)


def plan_video_scaling(info: dict, *, use_hardware: bool,
                       scale_vt_available: bool | None = None) -> VideoScalingPlan:
    """Plan the size and HDR conversion for one video re-encode.

    :param info: Probed stream facts (``video_width``, ``video_height``,
        ``video_color_transfer``). Missing facts keep the pre-existing
        behaviour of an unfiltered encode.
    :param use_hardware: Whether the encoder is h264_videotoolbox.
    :param scale_vt_available: Whether ffmpeg has ``scale_vt``; None probes
        this build (the seam tests use instead of a fake ffmpeg).
    :returns: The plan; one with no filter when the source needs no scaling.
    :raises TranscodeFailedError: An HDR source cannot be tone-mapped here.
    """
    if is_hdr_source(info):
        return _plan_hdr(info, use_hardware=use_hardware,
                         scale_vt_available=scale_vt_available)
    width, height = _dimensions(info)
    if height is None:
        return VideoScalingPlan()
    if width is not None:
        box = fit_within_output_box(width, height)
        if box is None:
            return VideoScalingPlan(output_height=height)
        out_height = box[1]
    elif height > TRANSCODE_MAX_HEIGHT:
        out_height = TRANSCODE_MAX_HEIGHT
    else:
        return VideoScalingPlan(output_height=height)
    return VideoScalingPlan(
        video_filter=(
            f"scale=w={TRANSCODE_MAX_WIDTH}:h={TRANSCODE_MAX_HEIGHT}"
            ":force_original_aspect_ratio=decrease:force_divisible_by=2"),
        output_height=out_height)
