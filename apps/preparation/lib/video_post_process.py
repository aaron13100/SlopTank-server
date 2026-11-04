#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Compose the full post-move video pipeline: repair, remux, normalize, transcode, subs."""

from __future__ import annotations

from pathlib import Path

from lib import media_update_tracker
from lib.container_remux import _fix_mkv_container, _remux_for_streaming
from lib.subtitle_pipeline import (
    SubtitleAcquisition,
    _extract_embedded_english_srt,
    acquire_subtitle_sidecars,
    _sync_sidecar_subtitles,
)
from lib.audio_normalize import normalize_audio
from lib.audio_reorder import reorder_preferred_audio
from lib.subtitle_embed import embed_finalized_subtitles
from lib.tv_audio_catchup_worker import (
    AudioPreparationError,
    prepare_audio_before_video_conversion,
)
from lib.transcode import (
    _transcode_for_streaming,
    enrich_streaming_plan_loudness,
    plan_streaming_work,
)


def _admit_write(before_write, path: Path, stage: str) -> None:
    """Invoke an optional whole-file writer admission callback."""
    if before_write is not None and before_write(path, stage) is False:
        raise RuntimeError(f"write admission refused before {stage}")


def _post_process_video(
        video_path: Path, *, before_write=None, path_changed=None,
        prefer_stream_copy: bool = False,
        subtitle_acquisition: SubtitleAcquisition | None = None) -> Path:
    """Run all post-move processing on a freshly-organized video. Subtitle
    acquisition runs against source bytes before any media rewrite. Order
    matters -- see deliverables/download-portable-media-design.md section 4:

      1 acquire subtitles, 2 fix MKV container, 3 reorder preferred audio,
      4 extract English subs, 5 legacy-container remux, 6 plan derived-audio
      loudness then normalize only when the transcode does not own that work,
      7 transcode (may append and conditionally loudness-correct a
      browser/TV-playable AAC companion), 8 reorder preferred audio again,
      9 sync remaining sidecars, 10 embed finalized subtitles and archive the
      now-redundant sidecar, 11 faststart only if still needed.

    ``subtitle_acquisition`` carries any pre-move acquisition; ``None`` starts
    acquisition here. Steps 5 and 7 may rename the file (extension change);
    the new path is
    tracked through the rest of the pipeline and reflected in the update tracker.
    When supplied, ``path_changed`` observes each completed rename immediately,
    before planning or later stages can fail.

    ``prefer_stream_copy`` uses the approved direct-play MP4 remux for an
    import whose only video work is HLS keyframe shortening. Full maintenance
    and playback-preparation callers retain their existing strict defaults.
    """
    if subtitle_acquisition is None:
        _admit_write(before_write, video_path, "subtitle acquisition")
        subtitle_acquisition = acquire_subtitle_sidecars(video_path)
    _admit_write(before_write, video_path, "MKV repair")
    _fix_mkv_container(video_path)
    _admit_write(before_write, video_path, "first audio-order rewrite")
    reorder_preferred_audio(video_path)
    _admit_write(before_write, video_path, "English subtitle extraction")
    _extract_embedded_english_srt(video_path)
    # Normalization, audio ordering and the final transcode already write
    # faststart MP4s. Defer that copy-only rewrite until those stages finish,
    # instead of reading and rewriting the whole movie just before doing it
    # again. Legacy containers still need their early MKV remux for planning.
    new_path = video_path
    if video_path.suffix.lower() not in (".mp4", ".m4v"):
        _admit_write(before_write, video_path, "streaming remux")
        new_path = _remux_for_streaming(video_path)
    if new_path != video_path:
        previous_path = video_path
        media_update_tracker.replace(video_path, new_path)
        video_path = new_path
        if path_changed is not None:
            path_changed(previous_path, video_path)
    # Enrich only the execution plan, never the library-wide selector: mean
    # volume requires decoding the full source stream. The normalizer consumes
    # the recorded source index so this full pipeline pays that decode once and
    # applies loudnorm only while encoding the derived AAC stereo companion.
    plan = plan_streaming_work(video_path)
    if plan is not None and plan.needs_video and plan.added_audio:
        _admit_write(before_write, video_path, "audio companion preparation")
        prepare_audio_before_video_conversion(video_path)
        plan = plan_streaming_work(video_path)
        if plan is None or plan.added_audio:
            raise AudioPreparationError(video_path, {
                "status": "planner_still_requires_audio",
                "error": "shared work planner still requests audio companions",
            })
    if plan is not None:
        plan = enrich_streaming_plan_loudness(video_path, plan)
    normalize_kwargs = {
        "loudness_measured_source_indexes": (
            plan.loudness_measured_source_indexes if plan is not None
            else frozenset()),
    }
    _admit_write(before_write, video_path, "audio normalization")
    normalized = normalize_audio(video_path, **normalize_kwargs)
    # A successful standalone normalization changed the file after planning;
    # discard the stale metadata and let transcode re-plan it. When no rewrite
    # occurred, reuse the enriched plan so its mean measurement cannot repeat.
    transcode_kwargs = {"plan": None if normalized else plan}
    if prefer_stream_copy:
        transcode_kwargs["prefer_stream_copy"] = True
    _admit_write(before_write, video_path, "streaming transcode")
    new_path = _transcode_for_streaming(video_path, **transcode_kwargs)
    if new_path != video_path:
        previous_path = video_path
        media_update_tracker.replace(video_path, new_path)
        video_path = new_path
        if path_changed is not None:
            path_changed(previous_path, video_path)
    _admit_write(before_write, video_path, "second audio-order rewrite")
    reorder_preferred_audio(video_path)
    _admit_write(before_write, video_path, "subtitle synchronization")
    _sync_sidecar_subtitles(
        video_path,
        skip=subtitle_acquisition.hash_paths_for_unchanged_media(video_path),
    )
    _admit_write(before_write, video_path, "subtitle embed")
    embed_finalized_subtitles(video_path)
    if video_path.suffix.lower() in (".mp4", ".m4v"):
        _admit_write(before_write, video_path, "streaming remux")
        video_path = _remux_for_streaming(video_path)
    return video_path
