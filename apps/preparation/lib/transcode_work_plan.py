# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Single-source-of-truth work planning for streaming conversions."""

from __future__ import annotations

import logging
from dataclasses import dataclass, replace
from pathlib import Path

from lib.audio_defaults import _probe_audio_streams
from lib.container_remux import _mp4_has_faststart
from lib.media_probe import _get_mean_volume, _probe_video_audio
from lib.organize_media_config import _MP4_COPYABLE_AUDIO_CODECS, _REMUX_FROM_EXTS
from lib.transcode_audio_plan import AddedAudioTrack, _plan_added_audio
from lib.transcode_command_plan import _target_suffix
from lib.transcode_video_policy import (
    _needs_reencode,
    _video_requires_reencode_except_gop,
)

log = logging.getLogger("organize-media")


@dataclass(frozen=True)
class StreamingWorkPlan:
    """The stream/container changes owed by one media file."""

    target_suffix: str
    needs_video: bool
    needs_audio: bool
    needs_container: bool
    needs_faststart: bool
    added_audio: tuple[AddedAudioTrack, ...] = ()
    loudness_measured_source_indexes: frozenset[int] = frozenset()

    @property
    def needed(self) -> bool:
        return (
            self.needs_video or self.needs_audio or self.needs_container
            or self.needs_faststart or bool(self.added_audio)
        )


def publication_suffix(path: Path, info: dict) -> str:
    """Return the container this file is published in, from its probed info.

    Audio planning done ahead of a conversion must judge playability in this
    container, never ``path.suffix``: 5.1 AAC plays on the TV inside MKV but
    is silent in MP4, so a companion judged against the source MKV was skipped
    and the MP4 conversion then refused the file (Airplane, 2026-10-10).
    """
    return _target_suffix(path, info)


def plan_streaming_work(path: Path,
                        info: dict | None = None) -> StreamingWorkPlan | None:
    """Return all streaming work owed, or None for an unprobeable file."""
    suffix = path.suffix.lower()
    if suffix in _REMUX_FROM_EXTS:
        return StreamingWorkPlan(".mkv", False, False, True, False)
    if info is None:
        info = _probe_video_audio(path)
    if not info:
        return None
    target_suffix = publication_suffix(path, info)
    video, audio, container = _needs_reencode(path, info, target_suffix)
    faststart = (
        target_suffix == ".mp4"
        and suffix in (".mp4", ".m4v")
        and not container
        and not _mp4_has_faststart(path)
    )
    return StreamingWorkPlan(
        target_suffix, video, audio, container, faststart,
        _plan_added_audio(path, target_suffix),
    )


def _default_loudness_track(
        plan: StreamingWorkPlan) -> tuple[int, AddedAudioTrack] | None:
    """Return the eligible default derived track and its plan position."""
    return next(
        ((position, track) for position, track in enumerate(plan.added_audio)
         if track.make_default and not track.is_commentary),
        None,
    )


def _measure_derived_audio_source(
        path: Path, track: AddedAudioTrack, *,
        audio_streams: list[dict] | None = None,
        probe_audio=None, measure_mean=None) -> tuple[bool, float | None]:
    """Measure a planned source once, distinguishing missing from unknown."""
    streams = audio_streams
    if streams is None:
        streams = (probe_audio or _probe_audio_streams)(path)
    audio_index = next(
        (position for position, stream in enumerate(streams or [])
         if stream.get("index") == track.source_index),
        None,
    )
    if audio_index is None:
        log.warning(
            "cannot measure derived audio source for %s: stream %s is missing",
            path.name, track.source_index,
        )
        return False, None
    return True, (measure_mean or _get_mean_volume)(path, audio_index=audio_index)


def enrich_streaming_plan_loudness(
        path: Path, plan: StreamingWorkPlan, *, enabled: bool,
        threshold_db: float, audio_streams: list[dict] | None = None,
        probe_audio=None, measure_mean=None) -> StreamingWorkPlan:
    """Mark an eligible derived default track for loudness correction."""
    if not enabled or not plan.added_audio or plan.loudness_measured_source_indexes:
        return plan
    selected = _default_loudness_track(plan)
    if selected is None:
        return plan
    default_position, track = selected
    measured, mean_volume = _measure_derived_audio_source(
        path, track, audio_streams=audio_streams,
        probe_audio=probe_audio, measure_mean=measure_mean,
    )
    if not measured:
        return plan
    additions = list(plan.added_audio)
    additions[default_position] = replace(
        track,
        apply_loudnorm=(mean_volume is not None and mean_volume <= threshold_db),
    )
    return replace(
        plan,
        added_audio=tuple(additions),
        loudness_measured_source_indexes=frozenset({track.source_index}),
    )


def needs_streaming_work(path: Path) -> bool:
    """Return whether any pipeline stage would rewrite the file."""
    plan = plan_streaming_work(path)
    return plan is not None and plan.needed


def plan_container_only(plan: StreamingWorkPlan, info: dict,
                        audio_streams: list[dict] | None) -> StreamingWorkPlan | None:
    """Narrow a full plan to work safely handled without video encoding."""
    if plan.needs_video and _video_requires_reencode_except_gop(info):
        log.info(
            "container-only pass: video is not browser-direct-play compatible; "
            "leaving for the full pass",
        )
        return None
    if not plan.needs_container:
        return None
    if plan.target_suffix == ".mp4":
        for stream in audio_streams or []:
            codec = (stream.get("codec_name") or "").lower()
            if codec and codec not in _MP4_COPYABLE_AUDIO_CODECS:
                log.info(
                    "container-only pass: %s cannot be copied into MP4, "
                    "leaving for the full pass", codec,
                )
                return None
    if plan.needs_video:
        log.info(
            "container-only pass: video copied; long GOP left for the "
            "scheduled pass",
        )
    return replace(plan, needs_video=False, needs_audio=False)


def plan_direct_play_remux(
        path: Path, info: dict, plan: StreamingWorkPlan,
        audio_streams: list[dict]) -> StreamingWorkPlan | None:
    """Prefer lossless video copying for a directly playable MP4 import.

    Reuse the container-only pass's codec and audio preservation checks.
    An existing MP4 needs no video encode solely for HLS keyframe spacing;
    any required audio/container work stays in its plan. Full maintenance
    still owns keyframe shortening. None retains the full conversion path.
    """
    if plan.target_suffix != ".mp4" or _video_requires_reencode_except_gop(info):
        return None
    if info.get("audio_codec") and not audio_streams:
        return None
    if plan.needs_container:
        return plan_container_only(plan, info, audio_streams)
    if path.suffix.lower() not in (".mp4", ".m4v"):
        return None
    return replace(plan, needs_video=False)
