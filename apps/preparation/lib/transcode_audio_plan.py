# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Companion-audio selection for portable streaming outputs."""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

from lib.audio_defaults import (
    _is_browser_playable_audio,
    _is_tv_playable_audio,
    _probe_audio_streams,
    _resolve_default_index,
)

_COMMENTARY_TITLE_RE = re.compile(r"commentary", re.IGNORECASE)


def _is_commentary_track(stream: dict) -> bool:
    """Recognize explicitly flagged or titled commentary tracks."""
    if (stream.get("disposition") or {}).get("comment"):
        return True
    title = str((stream.get("tags") or {}).get("title") or "")
    return bool(_COMMENTARY_TITLE_RE.search(title))


def _stream_language(stream: dict) -> str:
    """Return a normalized language tag, using und when absent."""
    return str(
        (stream.get("tags") or {}).get("language") or ""
    ).strip().lower() or "und"


@dataclass(frozen=True)
class AddedAudioTrack:
    """One browser/TV-playable AAC track planned from a source stream."""

    source_index: int
    language: str
    channels: int
    bitrate: str
    make_default: bool
    is_commentary: bool = False
    apply_loudnorm: bool = False


def _audio_sources_needing_companion(streams: list[dict],
                                     container_suffix: str) -> list[dict]:
    """Select the strongest source in each group lacking playable audio."""
    order: list[tuple[str, bool]] = []
    groups: dict[tuple[str, bool], list[dict]] = {}
    for stream in streams:
        key = (_stream_language(stream), _is_commentary_track(stream))
        if key not in groups:
            groups[key] = []
            order.append(key)
        groups[key].append(stream)
    return [
        max(groups[key], key=lambda candidate: int(candidate.get("channels") or 0))
        for key in order
        if not any(_is_browser_playable_audio(item) for item in groups[key])
        or not any(
            _is_tv_playable_audio(item, container_suffix) for item in groups[key]
        )
    ]


def _added_audio_from_streams(streams: list[dict],
                              container_suffix: str) -> tuple[AddedAudioTrack, ...]:
    """Plan one stereo AAC companion per language/commentary group."""
    to_add = _audio_sources_needing_companion(streams, container_suffix)
    if not to_add:
        return ()
    synthetic = [
        dict(source, codec_name="aac", channels=2, sample_rate="48000")
        for source in to_add
    ]
    resolved = _resolve_default_index(streams + synthetic, container_suffix)
    winning_position = (
        resolved - len(streams)
        if resolved is not None and resolved >= len(streams)
        else None
    )
    return tuple(
        AddedAudioTrack(
            source_index=source.get("index"),
            language=_stream_language(source),
            channels=int(source.get("channels") or 0),
            bitrate="160k",
            make_default=(position == winning_position),
            is_commentary=_is_commentary_track(source),
        )
        for position, source in enumerate(to_add)
    )


def _plan_added_audio(path: Path,
                      container_suffix: str) -> tuple[AddedAudioTrack, ...]:
    """Probe a file and plan its companion tracks, or return none."""
    streams = _probe_audio_streams(path)
    if not streams:
        return ()
    return _added_audio_from_streams(streams, container_suffix)
