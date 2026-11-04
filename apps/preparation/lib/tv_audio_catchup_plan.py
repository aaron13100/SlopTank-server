# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Planning and verification for additive TV-audio catch-up rewrites."""

from __future__ import annotations

from pathlib import Path

from lib.audio_defaults import _disposition_value
from lib.media_probe import _probe_video_audio
from lib.transcode import (
    AddedAudioTrack,
    StreamingWorkPlan,
    _added_audio_from_streams,
    enrich_streaming_plan_loudness,
    publication_suffix,
)
from lib.media_tools import require_tool


class VerifyError(Exception):
    """The source metadata or output cannot prove a safe additive rewrite."""


def source_path_error(path: Path) -> str | None:
    """Explain an unsafe manifest source, or return None for a regular/missing path."""
    if not path.is_absolute():
        return "source path must be absolute"
    if path.is_symlink():
        return "source path must not be a symlink"
    if path.exists() and not path.is_file():
        return "source path must be a regular file"
    return None


def _integer_metadata(value, *, minimum: int) -> int | None:
    """Parse ffprobe integer metadata without treating bool/unknown as zero."""
    if isinstance(value, bool) or not isinstance(value, (int, str)):
        return None
    try:
        parsed = int(value)
    except ValueError:
        return None
    return parsed if parsed >= minimum else None


def require_publication_suffix(path: Path, *, probe_video=None) -> str:
    """Return the container companions must play in, refusing if unknowable.

    The source is stream-copied into its own container, but the conversion
    that follows plans for the published one; judging the source container
    left that plan with companions this rewrite never added.
    """
    info: dict = {}
    if path.suffix.lower() == ".mkv":
        # Only an MKV's subtitle tracks can keep it MKV (_target_suffix).
        info = (probe_video or _probe_video_audio)(path) or {}
        if not info:
            raise VerifyError(
                f"cannot probe {path.name} to choose its publication container")
    return publication_suffix(path, info)


def plan_catchup_tracks(
        path: Path, streams: list[dict], *,
        container_suffix: str | None = None,
        measure_mean=None) -> tuple[AddedAudioTrack, ...]:
    """Reuse the pipeline's companion and conditional-loudness decisions."""
    seen_indexes: set[int] = set()
    for position, stream in enumerate(streams):
        index = _integer_metadata(stream.get("index"), minimum=0)
        channels = _integer_metadata(stream.get("channels"), minimum=1)
        if index is None or index in seen_indexes:
            raise VerifyError(
                f"source track {position} has an unusable or duplicate "
                f"stream index ({stream.get('index')!r})")
        if channels is None:
            raise VerifyError(
                f"source track {position} has an unusable channel count "
                f"({stream.get('channels')!r})")
        seen_indexes.add(index)
    container = container_suffix or require_publication_suffix(path)
    tracks = _added_audio_from_streams(streams, container)
    if not tracks:
        return ()
    plan = StreamingWorkPlan(
        container, False, False, False, False,
        added_audio=tracks,
    )
    return enrich_streaming_plan_loudness(
        path, plan, audio_streams=streams,
        measure_mean=measure_mean,
    ).added_audio


def build_add_track_cmd(path: Path, tmp: Path, streams: list[dict],
                        tracks: tuple[AddedAudioTrack, ...]) -> list[str]:
    """Copy existing streams and append the pipeline-planned AAC tracks."""
    ffmpeg = require_tool("ffmpeg")
    cmd = [ffmpeg, "-nostdin", "-y", "-i", str(path),
           "-map", "0", "-c", "copy"]
    if any(track.make_default for track in tracks):
        for i, stream in enumerate(streams):
            cmd += [f"-disposition:a:{i}", _disposition_value(stream, False)]
    for offset, track in enumerate(tracks):
        out_index = len(streams) + offset
        cmd += [
            "-map", f"0:{track.source_index}",
            f"-c:a:{out_index}", "aac",
            f"-ar:a:{out_index}", "48000",
            f"-ac:a:{out_index}", "2",
            f"-b:a:{out_index}", track.bitrate,
        ]
        if track.apply_loudnorm:
            cmd += [
                f"-filter:a:{out_index}",
                "loudnorm=I=-16:TP=-1.5:LRA=11",
            ]
        cmd += [
            f"-disposition:a:{out_index}",
            "default" if track.make_default else "0",
        ]
    cmd += ["-movflags", "+faststart", str(tmp)]
    return cmd


def verify_output(before: list[dict], after: list[dict],
                  tracks: tuple[AddedAudioTrack, ...]) -> None:
    """Require exactly the copied sources plus the planned AAC companions."""
    want = len(before) + len(tracks)
    if len(after) != want:
        raise VerifyError(f"expected {want} audio streams, got {len(after)}")
    for i, original in enumerate(before):
        want_codec = str(original.get("codec_name") or "").lower()
        got_codec = str(after[i].get("codec_name") or "").lower()
        want_channels = _integer_metadata(original.get("channels"), minimum=1)
        got_channels = _integer_metadata(after[i].get("channels"), minimum=1)
        if want_channels is None or got_channels is None:
            raise VerifyError(
                f"source track {i} has unusable channel metadata: "
                f"{original.get('channels')!r} -> {after[i].get('channels')!r}")
        if got_codec != want_codec or got_channels != want_channels:
            raise VerifyError(
                f"source track {i} changed: {want_codec}/{want_channels}ch "
                f"-> {got_codec}/{got_channels}ch")
    for offset, _track in enumerate(tracks):
        added = after[len(before) + offset]
        codec = str(added.get("codec_name") or "").lower()
        channels = _integer_metadata(added.get("channels"), minimum=1)
        rate = _integer_metadata(added.get("sample_rate"), minimum=1)
        if codec != "aac" or channels != 2 or rate != 48000:
            raise VerifyError(
                f"added track {offset} is {codec}/{channels}ch/{rate}Hz, "
                "want aac/2ch/48000Hz")
    defaults = [
        i for i, stream in enumerate(after)
        if (stream.get("disposition") or {}).get("default")
    ]
    planned_defaults = [
        len(before) + offset for offset, track in enumerate(tracks)
        if track.make_default
    ]
    expected_defaults = planned_defaults or [
        i for i, stream in enumerate(before)
        if (stream.get("disposition") or {}).get("default")
    ]
    if len(defaults) != 1 or defaults != expected_defaults:
        raise VerifyError(
            f"default flag on index(es) {defaults}, want {expected_defaults}")
