#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Interpret ffprobe stream JSON for the media pipeline."""

from __future__ import annotations


def _int_or_none(value) -> int | None:
    """Coerce an ffprobe field to an int, or None when absent/unusable.

    ffprobe reports missing numeric fields as the string "N/A" as well as by
    omitting them, so this must not raise on either.
    """
    try:
        return int(value) or None
    except (TypeError, ValueError):
        return None


def _empty_probe(format_data: dict) -> dict:
    return {
        "video_codec": None,
        "video_height": None,
        "video_width": None,
        # Colour facts decide whether the encode must tone-map HDR to SDR
        # (lib/transcode_video_scaling.py); None when ffprobe reports none.
        "video_color_transfer": None,
        "video_color_primaries": None,
        "video_color_space": None,
        "video_bit_rate": None,
        # These distinguish High 10 or an unsupported level from the plain
        # 8-bit High@4.x target a codec-name-only check would confuse it with.
        "video_profile": None,
        "video_pix_fmt": None,
        "video_level": None,
        "audio_codec": None,
        "audio_sample_rate": None,
        "audio_channels": None,
        "container": format_data.get("format_name", "") or "",
        "total_bit_rate": _int_or_none(format_data.get("bit_rate")),
        # The container total counts every copied audio stream, so sum all of
        # them before deriving the video's own rate.
        "audio_bit_rate_total": None,
        "subtitle_streams": [],
    }


def _set_video_facts(out: dict, stream: dict, codec_name: str) -> None:
    out["video_codec"] = codec_name
    try:
        out["video_height"] = int(stream.get("height") or 0) or None
    except (TypeError, ValueError):
        out["video_height"] = None
    out["video_width"] = _int_or_none(stream.get("width"))
    for key, field in (("video_color_transfer", "color_transfer"),
                       ("video_color_primaries", "color_primaries"),
                       ("video_color_space", "color_space")):
        out[key] = stream.get(field) or None
    # Often absent in Matroska; total_bit_rate remains the fallback.
    out["video_bit_rate"] = _int_or_none(stream.get("bit_rate"))
    profile = stream.get("profile")
    out["video_profile"] = (profile or None) and str(profile).lower()
    out["video_pix_fmt"] = stream.get("pix_fmt") or None
    out["video_level"] = _int_or_none(stream.get("level"))


def _add_audio_facts(out: dict, stream: dict, codec_name: str) -> None:
    rate = _int_or_none(stream.get("bit_rate"))
    if rate:
        out["audio_bit_rate_total"] = (out["audio_bit_rate_total"] or 0) + rate
    if out["audio_codec"] is not None:
        return
    out["audio_codec"] = codec_name
    try:
        out["audio_sample_rate"] = int(stream.get("sample_rate") or 0) or None
    except (TypeError, ValueError):
        out["audio_sample_rate"] = None
    try:
        out["audio_channels"] = int(stream.get("channels") or 0) or None
    except (TypeError, ValueError):
        out["audio_channels"] = None


def _subtitle_facts(stream: dict, codec_name: str) -> dict | None:
    try:
        index = int(stream.get("index"))
    except (TypeError, ValueError):
        return None
    tags = stream.get("tags") or {}
    disposition = stream.get("disposition") or {}
    language = (tags.get("language") or "").strip().lower()
    title = (tags.get("title") or "").strip()
    return {
        "index": index,
        "codec": codec_name,
        # These fields determine an extracted sidecar's filename so Jellyfin
        # re-attaches it as the same selectable track.
        "language": language or None,
        "title": title or None,
        "forced": bool(disposition.get("forced")),
        "default": bool(disposition.get("default")),
    }


def _parse_probe_streams(data: dict) -> dict:
    """Return normalized video, audio, container, and subtitle stream facts."""
    out = _empty_probe(data.get("format") or {})
    for stream in data.get("streams", []):
        codec_type = stream.get("codec_type")
        codec_name = (stream.get("codec_name") or "").lower()
        if codec_type == "video" and out["video_codec"] is None:
            _set_video_facts(out, stream, codec_name)
        elif codec_type == "audio":
            _add_audio_facts(out, stream, codec_name)
        elif codec_type == "subtitle":
            subtitle = _subtitle_facts(stream, codec_name)
            if subtitle is not None:
                out["subtitle_streams"].append(subtitle)
    return out
