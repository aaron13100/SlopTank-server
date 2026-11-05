# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
"""Subtitle/container selection and pure ffmpeg command construction."""

from __future__ import annotations

from pathlib import Path

from lib.audio_defaults import _disposition_value
from lib.mp4_start_alignment import (
    AlignmentPlan,
    alignment_container_args,
    alignment_filter_for_source,
    audio_alignment_ffmpeg_args,
    audio_anchor_bsf,
    plan_audio_alignment,
)
from lib.transcode_audio_plan import AddedAudioTrack
from lib.transcode_video_policy import _resolve_use_hardware, _video_encode_args
from lib.transcode_video_scaling import VideoScalingPlan, plan_video_scaling
from lib.media_tools import require_tool
from lib.remux_input_policy import remux_input_args

_TEXT_SUBTITLE_CODECS = {
    "subrip", "srt", "ass", "ssa", "mov_text", "text", "webvtt",
}
_SIDECAR_BITMAP_SUBTITLE_SUFFIXES = {"hdmv_pgs_subtitle": ".sup"}


def _build_realign_cmd(path: Path, tmp: Path, plan: AlignmentPlan) -> list[str]:
    """Copy non-audio streams and apply the existing audio alignment plan."""
    cmd = [
        require_tool("ffmpeg"), "-nostdin", "-y", "-i", str(path),
        "-map", "0", "-map_metadata", "0",
        "-c:v", "copy", "-c:s", "copy", "-c:d", "copy",
    ]
    cmd += audio_alignment_ffmpeg_args(plan)
    cmd += alignment_container_args(plan)
    cmd += ["-movflags", "+faststart", str(tmp)]
    return cmd

#: The longest cue gap an mp4's mov_text track can represent. ffmpeg's mov
#: muxer gives a mov_text track a 1,000,000 timescale and writes every
#: inter-cue gap as ONE 32-bit stts duration, so a gap above
#: 2**31 / 1_000_000 = 2147.483s is unrepresentable; the muxer then silently
#: collapses the WHOLE track to back-to-back cues starting at zero, cue
#: durations preserved and every gap reduced to one tick, with only a
#: warning ("Packet duration ... is out of range"). Measured 2026-09-29 on
#: this ffmpeg build with controls either side of the boundary: a 2040s
#: leading gap survives conversion intact, a 2200s one comes out zero-based
#: -- the exact defect that played The Martian Extended's forced English
#: track (first cue 5049s in) over the opening titles (c298). 2100s leaves
#: margin below the hard boundary. Matroska is unaffected (arbitrary block
#: timestamps), so this limit applies only to the mp4/mov_text path.
MP4_TEXT_GAP_LIMIT_SECONDS = 2100.0


def text_gap_overflow(cue_spans, *, limit: float = MP4_TEXT_GAP_LIMIT_SECONDS):
    """The first cue gap an mp4 mov_text track cannot represent, or None.

    :param cue_spans: Ordered ``(start_seconds, end_seconds)`` pairs of one
        track's cues. The track's leading silence (its first cue's start,
        which the mp4 muxer writes as the first stts gap) and every inter-cue
        gap both count: a leading overflow zeroes the whole track, and a
        mid-track overflow corrupts every cue after it (measured: a cue kept
        at 100s while the next, 2300s later, collapsed onto its
        predecessor).
    :param limit: :data:`MP4_TEXT_GAP_LIMIT_SECONDS`.
    :returns: The first overflowing gap in seconds, or None when every gap
        fits and the track is safe to embed as mov_text.
    """
    previous_end = None
    for start, end in cue_spans:
        gap = start if previous_end is None else start - previous_end
        if gap > limit:
            return gap
        previous_end = end
    return None


def _bitmap_subtitle_streams(info: dict) -> list[dict]:
    """Return every bitmap rather than text subtitle stream."""
    return [
        stream for stream in (info.get("subtitle_streams") or [])
        if stream.get("codec") not in _TEXT_SUBTITLE_CODECS
    ]


def _target_suffix(path: Path, info: dict) -> str:
    """Choose MP4 unless an MKV contains an unextractable bitmap track."""
    if path.suffix.lower() != ".mkv":
        return ".mp4"
    unextractable = [
        stream for stream in _bitmap_subtitle_streams(info)
        if stream.get("codec") not in _SIDECAR_BITMAP_SUBTITLE_SUFFIXES
    ]
    return ".mkv" if unextractable else ".mp4"


def _unrepresentable_text_indexes(info: dict) -> set[int]:
    """Global indexes of text streams the mp4 muxer cannot time correctly.

    Read from the ``mp4_text_gap_overflow`` mapping (``{index: seconds}``)
    that the sidecar-extraction stage measured into ``info``
    (:func:`lib.transcode_subtitle_io.extract_mp4_incompatible_subtitle_sidecars`),
    the same measured-facts-on-info pattern the audio start alignment uses.
    An absent key means nobody measured, which keeps every hand-built
    ``info`` dict and each pre-existing caller on the old behaviour of
    mapping every text stream (design invariant: no facts, no change).
    """
    return set(info.get("mp4_text_gap_overflow") or {})


def _sidecar_subtitle_targets(path: Path, info: dict) -> list[tuple[int, Path]]:
    """Name extractable subtitle sidecars for mp4-incompatible tracks.

    Two classes cost a sidecar: bitmap tracks (no mp4 codec exists for them)
    and text tracks whose cue gaps overflow the mov_text muxer's int32 stts
    range -- mapping one of those into an mp4 would silently zero-base the
    whole track (see :data:`MP4_TEXT_GAP_LIMIT_SECONDS`), so it is extracted
    to an .srt Jellyfin reads beside the video instead.
    """
    overflowing = _unrepresentable_text_indexes(info)
    sidecar_streams = list(_bitmap_subtitle_streams(info))
    sidecar_streams += [
        stream for stream in (info.get("subtitle_streams") or [])
        if stream.get("index") in overflowing
    ]
    targets: list[tuple[int, Path]] = []
    used: set[Path] = set()
    for stream in sidecar_streams:
        if stream.get("codec") in _TEXT_SUBTITLE_CODECS:
            suffix = ".srt"
        else:
            suffix = _SIDECAR_BITMAP_SUBTITLE_SUFFIXES.get(stream.get("codec"))
        index = stream.get("index")
        if suffix is None or index is None:
            continue
        stem = f"{path.stem}.{stream.get('language') or 'und'}"
        if stream.get("forced"):
            stem += ".forced"
        if stream.get("default"):
            stem += ".default"
        target = path.with_name(stem + suffix)
        ordinal = 2
        while target in used:
            target = path.with_name(f"{stem}.{ordinal}{suffix}")
            ordinal += 1
        used.add(target)
        targets.append((index, target))
    return targets


def _text_subtitle_indexes(info: dict) -> list[int]:
    """Return global indexes of text subtitle streams safe to map into an mp4.

    A text stream whose measured cue gaps overflow the mp4 muxer's int32 stts
    range (``mp4_text_gap_overflow`` on ``info``; see
    :data:`MP4_TEXT_GAP_LIMIT_SECONDS`) is excluded: mapping it as mov_text
    would collapse the track onto the film's opening (c298). Its subtitles
    survive as an .srt sidecar instead (``_sidecar_subtitle_targets``).
    """
    unrepresentable = _unrepresentable_text_indexes(info)
    return [
        stream["index"] for stream in (info.get("subtitle_streams") or [])
        if stream.get("codec") in _TEXT_SUBTITLE_CODECS
        and stream.get("index") is not None
        and stream["index"] not in unrepresentable
    ]


def _subtitle_mapping(info: dict, include_subs: bool,
                      target_suffix: str) -> tuple[list[dict], list[str]]:
    """Select subtitle streams and build their input mappings."""
    subtitle_streams = info.get("subtitle_streams") or []
    if target_suffix == ".mkv":
        mapped_subs = subtitle_streams if include_subs else []
    else:
        text_indexes = set(_text_subtitle_indexes(info))
        mapped_subs = [
            stream for stream in subtitle_streams
            if stream.get("index") in text_indexes
        ] if include_subs else []
    args: list[str] = []
    for stream in mapped_subs:
        args += ["-map", f"0:{stream.get('index')}"]
    if target_suffix == ".mkv" and include_subs:
        args += ["-map", "0:t?"]
    return mapped_subs, args


def _added_audio_args(added_audio: tuple[AddedAudioTrack, ...],
                      audio_streams: list[dict] | None,
                      alignment_plan: AlignmentPlan | None = None) -> list[str]:
    """Build mappings, codec settings, filters, and dispositions.

    Under a needed ``alignment_plan`` each added track is a fresh aac encode
    of its source stream, so it takes that source's shift ahead of any
    loudnorm and is anchored on the video's first DTS like every other
    audio track (c294); ``_resolve_alignment_plan`` guarantees the source is
    in the plan.
    """
    args: list[str] = []
    original_audio_count = len(audio_streams or [])
    if any(track.make_default for track in added_audio):
        for index in range(original_audio_count):
            args += [
                f"-disposition:a:{index}",
                _disposition_value(audio_streams[index], False),
            ]
    for offset, track in enumerate(added_audio):
        output_index = original_audio_count + offset
        args += [
            "-map", f"0:{track.source_index}",
            f"-c:a:{output_index}", "aac",
            f"-ar:a:{output_index}", "48000",
            f"-ac:a:{output_index}", "2",
            f"-b:a:{output_index}", track.bitrate,
        ]
        chain = []
        if alignment_plan is not None:
            shift = alignment_filter_for_source(alignment_plan, track.source_index)
            if shift is not None:
                chain.append(shift)
        if track.apply_loudnorm:
            chain.append("loudnorm=I=-16:TP=-1.5:LRA=11")
        if chain:
            args += [f"-filter:a:{output_index}", ",".join(chain)]
        if alignment_plan is not None:
            args += [f"-bsf:a:{output_index}",
                     audio_anchor_bsf(alignment_plan.anchor_dts)]
        args += [
            f"-disposition:a:{output_index}",
            "default" if track.make_default else "0",
        ]
    return args


def _resolve_alignment_plan(
        info: dict, needs_video: bool, *, target_suffix: str = ".mp4",
        added_audio: tuple[AddedAudioTrack, ...] = ()) -> AlignmentPlan | None:
    """Return the NEEDED audio start-alignment plan for this output, or None.

    None whenever the alignment probe never ran -- every hand-built ``info``
    dict in the existing test suite, and any caller that predates c212 --
    which keeps this a pure addition: no alignment keys means byte-identical
    behaviour to before this function existed (design.md invariant (d)).

    Also None whenever the video is being re-encoded (``needs_video``):
    design.md step 3, the output's own first DTS is not knowable before that
    encode runs, so the plan cannot be computed against it. The encoded MP4
    is measured and realigned if needed before the normal output verification
    in ``lib.transcode_execution``.

    Also None for a non-mp4 target (c294): only an mp4 hides a start offset
    in an edit list. Matroska keeps every block's own timestamp, so copied
    audio is already in sync there, and re-encoding it onto the video's
    first DTS only moved it (measured: 0.26 s off on a video-starts-late
    MKV).

    Also None when an added companion track's source stream is not in the
    plan: that track could not be shifted or anchored, and an unanchored
    track drags every other track's first sample under ``-use_editlist 0``.

    :param info: The probe dict a caller built for this output. Reads
        ``video_first_pts``/``video_first_dts``/``audio_starts``, all of
        which are absent unless something called
        ``lib.mp4_start_alignment.probe_start_alignment`` and merged its
        result in.
    :param needs_video: Whether this output's video is being re-encoded.
    :param target_suffix: The output container's suffix.
    :param added_audio: Companion tracks this output will also encode.
    """
    if needs_video or target_suffix != ".mp4":
        return None
    video_first_pts = info.get("video_first_pts")
    video_first_dts = info.get("video_first_dts")
    audio_starts = info.get("audio_starts")
    if video_first_pts is None or video_first_dts is None or not audio_starts:
        return None
    plan = plan_audio_alignment(video_first_pts, video_first_dts, audio_starts)
    if not plan.needed:
        return None
    planned_sources = {alignment.stream.stream_index for alignment in plan.alignments}
    if any(track.source_index not in planned_sources for track in added_audio):
        return None
    return plan


def _build_transcode_cmd(path: Path, tmp: Path, info: dict, needs_video: bool,
                         needs_audio: bool, include_subs: bool = True,
                         target_suffix: str = ".mp4",
                         added_audio: tuple[AddedAudioTrack, ...] = (),
                         audio_streams: list[dict] | None = None,
                         use_hardware: bool | None = None,
                         scale_vt_available: bool | None = None) -> list[str]:
    """Build the complete ffmpeg argument vector for one planned output."""
    ffmpeg = require_tool("ffmpeg")
    audio_channels = info.get("audio_channels") or 0
    use_hardware = _resolve_use_hardware(use_hardware)
    scaling = (plan_video_scaling(
        info, use_hardware=use_hardware, scale_vt_available=scale_vt_available)
               if needs_video else VideoScalingPlan())
    command = [
        ffmpeg, "-nostdin", "-y", *scaling.input_args,
        *(remux_input_args(path, target_suffix) if not needs_video else []),
        "-i", str(path),
        "-map", "0:v:0", "-map", "0:a?",
    ]
    mapped_subs, subtitle_args = _subtitle_mapping(
        info, include_subs, target_suffix,
    )
    command += subtitle_args
    if needs_video:
        if scaling.video_filter:
            command += ["-vf", scaling.video_filter]
        command += _video_encode_args(
            info.get("video_height"), info, use_hardware=use_hardware,
            scaling=scaling,
        )
    else:
        command += ["-c:v", "copy"]
    alignment_plan = _resolve_alignment_plan(
        info, needs_video, target_suffix=target_suffix, added_audio=added_audio)
    if alignment_plan is not None:
        command += audio_alignment_ffmpeg_args(alignment_plan)
    elif needs_audio:
        bitrate = "384k" if audio_channels and audio_channels > 2 else "192k"
        command += ["-c:a", "aac", "-ar", "48000", "-b:a", bitrate]
    else:
        command += ["-c:a", "copy"]
    if added_audio:
        command += _added_audio_args(added_audio, audio_streams, alignment_plan)
    if mapped_subs and target_suffix == ".mkv":
        command += ["-c:s", "copy", "-c:t", "copy"]
    elif mapped_subs:
        command += ["-c:s", "mov_text"]
    if target_suffix == ".mp4":
        command += ["-movflags", "+faststart"]
        if alignment_plan is not None:
            command += alignment_container_args(alignment_plan)
    command.append(str(tmp))
    return command
