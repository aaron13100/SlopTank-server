#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Compatibility facade for the portable streaming conversion pipeline."""

from __future__ import annotations

import logging
import shutil  # test seam: tests patch transcode.shutil.which (this same
               # global module); require_tool resolves through it, so it stays.
from pathlib import Path
from lib.audio_defaults import (
    _disposition_value,
    _is_browser_playable_audio,
    _is_tv_playable_audio,
    _probe_audio_streams,
    _resolve_default_index,
)
from lib.container_remux import _move_sidecars_to_new_stem, _mp4_has_faststart
from lib.encode_priority import (
    _clamp_at_launch,
    _host_attended_check,
    _priority_prefix,
    _run_supervised,
    run_encode,
)
from lib.media_probe import (
    _ffmpeg_stderr_tail,
    _get_mean_volume,
    _max_keyframe_gap,
    _probe_video_audio,
)
from lib.media_write_lock import exclusive_media_write
from lib.organize_media_config import (
    ADAPTIVE_ENCODE_THROTTLE,
    ADAPTIVE_THROTTLE_MIN_IDLE_SECONDS,
    ADAPTIVE_THROTTLE_POLL_SECONDS,
    CONTAINER_ONLY_PASS,
    ENCODE_STALL_POLL_SECONDS,
    ENCODE_STALL_SECONDS,
    NORMALIZE_AUDIO,
    NORMALIZE_AUDIO_THRESHOLD_DB,
    TRANSCODE_FOR_STREAMING,
    TRANSCODE_NICE,
)
from lib.transcode_audio_plan import (
    AddedAudioTrack,
    _added_audio_from_streams,
    _audio_sources_needing_companion,
    _is_commentary_track,
    _plan_added_audio,
    _stream_language,
)
from lib.transcode_command_plan import (
    _SIDECAR_BITMAP_SUBTITLE_SUFFIXES,
    _TEXT_SUBTITLE_CODECS,
    _added_audio_args,
    _bitmap_subtitle_streams,
    _build_transcode_cmd,
    _sidecar_subtitle_targets,
    _subtitle_mapping,
    _target_suffix,
    _text_subtitle_indexes,
)
from lib.transcode_execution import (
    _execute_transcode,
    _finalize_transcode,
    _log_transcode_failure,
    _output_plan_for_validation,
    _plan_transcode_retry,
    _resolve_transcode_plan,
    _run_transcode,
    _should_retry_in_software,
    _should_retry_without_subs,
    _transcode_commands,
    _verify_transcode_output,
)
from lib.transcode_subtitle_io import discard_sidecars as _discard_sidecars
from lib.transcode_subtitle_io import extract_mp4_incompatible_subtitle_sidecars
from lib.transcode_types import TranscodeFailedError
from lib.transcode_video_policy import (
    _has_videotoolbox,
    _hw_bitrate_for_height,
    _needs_reencode,
    _source_capped_hw_bitrate,
    _video_encode_args,
    _video_requires_reencode_except_gop,
)
from lib.transcode_work_plan import (
    StreamingWorkPlan,
    _default_loudness_track,
    _measure_derived_audio_source,
    needs_streaming_work,
    plan_container_only,
    plan_direct_play_remux,
    plan_streaming_work,
    publication_suffix,
)
from lib.transcode_work_plan import enrich_streaming_plan_loudness as _enrich_loudness
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")


def enrich_streaming_plan_loudness(
        path: Path, plan: StreamingWorkPlan, *,
        audio_streams: list[dict] | None = None,
        measure_mean=None) -> StreamingWorkPlan:
    """Apply current facade configuration to derived-track loudness policy."""
    return _enrich_loudness(
        path,
        plan,
        enabled=TRANSCODE_FOR_STREAMING and NORMALIZE_AUDIO,
        threshold_db=NORMALIZE_AUDIO_THRESHOLD_DB,
        audio_streams=audio_streams,
        probe_audio=_probe_audio_streams,
        measure_mean=measure_mean or _get_mean_volume,
    )


def _extract_mp4_incompatible_subtitle_sidecars(
        path: Path, info: dict) -> list[Path]:
    """Extract sidecars through the facade's patchable encode entry point."""
    return extract_mp4_incompatible_subtitle_sidecars(path, info, encode=run_encode)


def _prepare_transcode_output(
        path: Path, info: dict, target_suffix: str, *,
        extract_bitmap_subtitles=None) -> list[Path]:
    """Extract MP4-incompatible subtitles ahead of the encode.

    The output temp itself is owned by `exclusive_media_write` in
    `_transcode_for_streaming`, which holds the target's lock from before
    the encode starts until after install; this helper only extracts the
    sidecars, through an injectable seam for tests.
    """
    extract_bitmap_subtitles = (extract_bitmap_subtitles
                                or _extract_mp4_incompatible_subtitle_sidecars)
    if target_suffix == ".mp4":
        return extract_bitmap_subtitles(path, info)
    return []


def _log_transcode_plan(path: Path, info: dict, plan: StreamingWorkPlan,
                        *, write_log=None) -> None:
    """Report the exact stream changes a prepared plan will make."""
    (write_log or log.info)(
        "transcode for streaming: %s (v=%s%s, a=%s%s%s) -> %s",
        path.name,
        info.get("video_codec") or "?",
        " -> h264" if plan.needs_video else "",
        info.get("audio_codec") or "?",
        " -> aac" if plan.needs_audio else "",
        f", +{len(plan.added_audio)} aac track(s)" if plan.added_audio else "",
        plan.target_suffix,
    )


def _transcode_for_streaming(path: Path, *, plan: StreamingWorkPlan | None = None,
                             prefer_stream_copy: bool = False,
                             run_transcode=None) -> Path:
    """Apply a streaming plan, optionally preferring the existing MP4 remux.

    Copy preference is scoped to new imports. Maintenance retains the full
    keyframe policy, and incompatible video always retains its full encode.
    """
    if not TRANSCODE_FOR_STREAMING or not path.exists():
        return path
    require_tool("ffmpeg")
    info = _probe_video_audio(path)
    if not info:
        return path
    plan = plan or plan_streaming_work(path, info)
    if plan is None:
        return path
    direct_play_remux = False
    preferred_audio_streams = None
    if (prefer_stream_copy and not CONTAINER_ONLY_PASS
            and plan.target_suffix == ".mp4"
            and not _video_requires_reencode_except_gop(info)):
        candidate_audio_streams = _probe_audio_streams(path)
        copy_plan = plan_direct_play_remux(path, info, plan, candidate_audio_streams)
        if copy_plan is not None:
            direct_play_remux = True
            preferred_audio_streams = candidate_audio_streams
            if plan.needs_video:
                log.info(
                    "direct-play import: copying compatible video in %s; "
                    "keyframe shortening remains with scheduled maintenance",
                    path.name)
            plan = copy_plan
    resolved = _resolve_transcode_plan(
        path, info, plan, container_only=CONTAINER_ONLY_PASS,
        probe_audio=_probe_audio_streams,
    )
    if resolved is None:
        return path
    plan, audio_streams = resolved
    if audio_streams is None:
        audio_streams = preferred_audio_streams
    if plan.added_audio:
        if audio_streams is None:
            audio_streams = _probe_audio_streams(path)
        plan = enrich_streaming_plan_loudness(
            path, plan, audio_streams=audio_streams,
        )
    _log_transcode_plan(path, info, plan)
    # The lock is held from before the encode starts until after install:
    # _execute_transcode runs the encode, verifies, and installs, all under
    # this with-block. A MediaWriteBusy propagates to the caller's own
    # per-file boundary (backfill's _convert_one, organize-media's
    # handle_folder, fix-keyframes-catchup's _reencode_worklist), which logs
    # it with the path and records this file as not converted, never as a
    # success, without stopping the batch.
    with exclusive_media_write(
            path, "transcoding", suffix=plan.target_suffix) as tmp:
        sidecars = _prepare_transcode_output(path, info, plan.target_suffix)
        command, no_subs, software = _transcode_commands(
            path, tmp, info, plan.needs_video, plan.needs_audio,
            target_suffix=plan.target_suffix, added_audio=plan.added_audio,
            audio_streams=audio_streams,
        )
        execute_kwargs = {
            "container_only": CONTAINER_ONLY_PASS or direct_play_remux,
            "run_transcode": run_transcode or _run_transcode,
        }
        return _execute_transcode(
            path, tmp, plan.target_suffix, sidecars, command, no_subs, software,
            **execute_kwargs,
        )
