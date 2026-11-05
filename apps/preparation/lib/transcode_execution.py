# SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05.
"""Retry, verification, and replacement mechanics for streaming conversion."""

from __future__ import annotations

import logging
import os
import re
import stat
import tempfile
import time
from dataclasses import replace
from pathlib import Path

from lib.media_identity import replace_media_file
from lib.container_remux import _move_sidecars_to_new_stem
from lib.encode_cancellation import EncodeCancelled
from lib.encode_priority import run_encode
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.file_ops import unique_path
from lib.media_duration import get_media_duration
from lib.media_probe import _ffmpeg_stderr_tail, _probe_video_audio
from lib.mp4_start_alignment import (
    ALIGN_THRESHOLD_SECONDS,
    AlignmentProbeError,
    first_packet_gap_problem,
    opening_gap_allowance,
    plan_audio_alignment,
    probe_opening_packets,
    probe_start_alignment,
)
from lib.transcode_command_plan import _build_realign_cmd, _build_transcode_cmd
from lib.transcode_subtitle_io import (
    discard_sidecars,
    probe_text_track_cue_facts,
)
from lib.transcode_types import TranscodeFailedError
from lib.transcode_video_policy import _video_requires_reencode_except_gop
from lib.transcode_work_plan import (
    StreamingWorkPlan,
    plan_container_only,
    plan_streaming_work,
)

log = logging.getLogger("organize-media")
_FRAME_COUNT_RE = re.compile(r"frame=\s*(\d+)")


def _should_retry_without_subs(stderr: str | None) -> bool:
    """Recognize subtitle conversion or output-tag failures."""
    lowered = (stderr or "").lower()
    return (
        "subtitle codec" in lowered or "subtitle encoding" in lowered
        or "could not find tag" in lowered
    )


def _should_retry_in_software(command: list[str], result) -> bool:
    """Retry hardware failure only when it produced explicitly zero frames."""
    if result.returncode == 0 or "h264_videotoolbox" not in command:
        return False
    frames = [int(match) for match in _FRAME_COUNT_RE.findall(result.stderr or "")]
    return bool(frames) and max(frames) == 0


def _log_transcode_failure(path: Path, result, attempt: str) -> None:
    log.warning(
        "transcode %s for %s: rc=%s; ffmpeg said: %s",
        attempt, path.name, result.returncode, _ffmpeg_stderr_tail(result),
    )


def _plan_transcode_retry(cmd: list[str], result,
                          no_subs_cmd: list[str] | None,
                          software_cmd: list[str] | None):
    """Choose at most one disjoint recovery attempt."""
    candidates = (
        (no_subs_cmd, "no subs", _should_retry_without_subs(result.stderr)),
        (software_cmd, "software encoder",
         _should_retry_in_software(cmd, result)),
    )
    for candidate, label, applies in candidates:
        if candidate is not None and candidate != cmd and applies:
            return candidate, label
    return None


def _log_encode_speed(path: Path, tmp: Path, elapsed: float,
                      duration_of) -> None:
    """Log how long an encode took and how fast it ran against realtime.

    The realtime factor is output duration over wall time: below 1x a
    conversion cannot keep up with playback length, which is how a
    never-finishing 2160p encode shows up before it burns a whole night.
    """
    size = tmp.stat().st_size
    duration = duration_of(tmp)
    factor = (f"{duration / elapsed:.2f}x realtime"
              if duration and elapsed > 0 else "realtime factor unknown")
    log.info("encoded %s in %.0fs: %d bytes, %s",
             path.name, elapsed, size, factor)


def _run_transcode(path: Path, tmp: Path, cmd: list[str],
                   no_subs_cmd: list[str] | None = None,
                   software_cmd: list[str] | None = None,
                   *, encode=None, clock=None, duration_of=None) -> bool:
    """Run an encode and its single eligible recovery attempt.

    :param clock: Injectable monotonic clock for the completion speed line.
    :param duration_of: Injectable output-duration probe for that line.
    """
    encode = encode or run_encode
    clock = clock or time.monotonic
    started = clock()
    result = encode(cmd, timeout=28800, progress_path=tmp)
    if result.returncode == 0 and tmp.exists() and tmp.stat().st_size > 0:
        _log_encode_speed(path, tmp, clock() - started,
                          duration_of or get_media_duration)
        return True
    _log_transcode_failure(path, result, "failed")
    tmp.unlink(missing_ok=True)
    retry = _plan_transcode_retry(
        cmd, result, no_subs_cmd, software_cmd,
    )
    if retry is None:
        return False
    retry_command, label = retry
    log.info("retrying %s with %s", path.name, label)
    result = encode(retry_command, timeout=28800, progress_path=tmp)
    if result.returncode == 0 and tmp.exists() and tmp.stat().st_size > 0:
        log.info("recovered %s on the %s retry", path.name, label)
        return True
    _log_transcode_failure(path, result, f"retry ({label}) failed")
    tmp.unlink(missing_ok=True)
    return False


def _output_plan_for_validation(
        info: dict, plan: StreamingWorkPlan, *,
        allow_deferred_long_gop: bool = False) -> StreamingWorkPlan:
    """Apply the container-only pass's single verification exception."""
    if (allow_deferred_long_gop and plan.needs_video
            and not _video_requires_reencode_except_gop(info)):
        return replace(plan, needs_video=False)
    return plan


def _mp4_start_alignment_problem(tmp: Path, *, source: Path | None = None,
                                 probe_alignment=None,
                                 probe_opening=None) -> str | None:
    """Return a rejection reason when tmp's tracks do not start together.

    The c212 L4 net: probes the OUTPUT itself (never trusts the command that
    produced it), so this catches any MP4-writing path that leaves a
    stream-copied audio track starting away from the video's first decode
    time -- this builder's own bug, a future change to it, or (were one ever
    routed through this same verification) a different writer entirely.
    Non-MP4 outputs have no edit-list concept and are not checked.

    Start times alone cannot see the c294 failure: a muxer that drags only a
    track's first sample to the origin leaves every start time at 0 both
    ways while the rest of the track stays where it was. So the video track
    and every audio track are also checked for a first DTS gap out of line
    with their steady spacing (`first_packet_gap_problem`), beyond whatever
    the same track of `source` already had (a VFR source can open with a
    long first frame; carrying it through is not a defect).

    :param source: The file the output was written from, or None when it is
        unknown (then no inherited first gap is allowed).
    :param probe_alignment: Injectable seam for tests; defaults to the real
        `lib.mp4_start_alignment.probe_start_alignment`.
    :param probe_opening: Injectable seam for tests; defaults to the real
        `lib.mp4_start_alignment.probe_opening_packets`.
    :returns: None when aligned or genuinely not applicable (non-mp4, or no
        video stream at all); a message naming every offending stream when
        misaligned; or ``"could not measure start alignment: <detail>"``
        when a probe raised `AlignmentProbeError` -- an unread output must
        be rejected, never silently passed as aligned.
    """
    if tmp.suffix.lower() != ".mp4":
        return None
    probe_alignment = probe_alignment or probe_start_alignment
    probe_opening = probe_opening or probe_opening_packets
    try:
        offenders = []
        probe = probe_alignment(tmp)
        if probe is not None:
            # Measured streams only: an unmeasurable one is reported by the
            # spacing check below if it was written wrong, never assumed at 0.
            for alignment in plan_audio_alignment(
                    probe.video_first_pts, probe.video_first_dts,
                    probe.audio_starts).alignments:
                if abs(alignment.shift) >= ALIGN_THRESHOLD_SECONDS:
                    offenders.append(
                        f"audio stream {alignment.stream.index} shifted "
                        f"{alignment.shift:+.3f}s from the video's first decode time")
        source_opening = probe_opening(source) if source is not None else {}
        for label, packets in probe_opening(tmp).items():
            problem = first_packet_gap_problem(
                [dts for _, dts in packets if dts is not None],
                allowance=opening_gap_allowance(source_opening.get(label, [])))
            if problem is not None:
                offenders.append(f"{label} {problem}")
    except AlignmentProbeError as exc:
        return f"could not measure start alignment: {exc.detail}"
    if not offenders:
        return None
    return f"start-of-file alignment ({', '.join(offenders)})"


#: How far an output text track's first cue may sit from its source track's.
#: The muxer's own measured write shift is ~23ms (see subtitle_embed's
#: _TIMING_TOLERANCE_MS note); a collapsed track moves by whole seconds to
#: minutes, so 0.1s separates "same timing" from "zero-based" with margin
#: both ways (c298).
_SUBTITLE_TIMING_TOLERANCE_SECONDS = 0.1


def _mp4_text_timing_problem(tmp: Path, *, source: Path,
                             probe_facts=None, probe_alignment=None) -> str | None:
    """Return a rejection reason when an output text track lost its timing.

    The c298 net: ffmpeg's mp4 muxer collapses a mov_text track whose cue
    gaps exceed its int32 stts range to back-to-back cues from zero (cue
    durations preserved, every gap one tick), so the track's first cue start
    is the observable that survives the write wrong. Every text track of the
    OUTPUT must start at the same position relative to the video's first
    presentation time as some same-language SOURCE track. This permits a
    uniform muxer origin shift while catching a zero-based track --
    this builder's own bug, a future change, or a different writer routed
    through the same verification -- regardless of which gap caused it.
    Probes the output itself, never the command that produced it.

    :param tmp: The produced output.
    :param source: The file the output was written from.
    :param probe_facts: Injectable seam for tests; defaults to the real
        `lib.transcode_subtitle_io.probe_text_track_cue_facts`.
    :returns: None when the output has no text tracks or every one keeps its
        source timing; a message naming the offending stream otherwise; or
        ``"could not measure subtitle timing: <detail>"`` when a probe
        failed -- an unread output is rejected, never passed as timed.
    """
    probe_facts = probe_facts or probe_text_track_cue_facts
    try:
        source_facts = probe_facts(source)
        output_facts = probe_facts(tmp)
    except TranscodeFailedError as exc:
        return f"could not measure subtitle timing: {exc}"
    if not output_facts:
        return None
    try:
        source_video = (probe_alignment or probe_start_alignment)(source)
        output_video = (probe_alignment or probe_start_alignment)(tmp)
    except AlignmentProbeError as exc:
        return f"could not measure subtitle video timeline: {exc.detail}"
    if source_video is None or output_video is None:
        return "could not measure subtitle video timeline: video stream missing"
    problems = []
    for track in output_facts.values():
        candidates = [
            facts.first_cue_start - source_video.video_first_pts
            for facts in source_facts.values()
            if track.language is None or facts.language in (None, track.language)
        ] or [facts.first_cue_start - source_video.video_first_pts
              for facts in source_facts.values()]
        output_start = track.first_cue_start - output_video.video_first_pts
        if candidates and not any(
                abs(output_start - start) <= _SUBTITLE_TIMING_TOLERANCE_SECONDS
                for start in candidates):
            problems.append(
                f"text stream {track.index} starts at {output_start:.3f}s "
                f"relative to video, but the source's text tracks start at "
                f"{', '.join(f'{start:.3f}s' for start in sorted(candidates))}")
    if not problems:
        return None
    return ("subtitle track(s) lost their source timing, the mp4 muxer's "
            "int32 gap collapse (c298): " + "; ".join(problems))


def _verify_transcode_output(
        path: Path, tmp: Path, *, allow_deferred_long_gop: bool = False,
        probe=None, planner=None, probe_alignment=None,
        probe_opening=None, probe_cues=None) -> None:
    """Refuse an output that cannot be proved to meet its work plan."""
    info = (probe or _probe_video_audio)(tmp)
    plan = (planner or plan_streaming_work)(tmp, info) if info else None
    if plan is None:
        tmp.unlink(missing_ok=True)
        raise TranscodeFailedError(
            f"could not probe the transcode output for {path.name}; "
            "original file preserved",
        )
    plan = _output_plan_for_validation(
        info, plan, allow_deferred_long_gop=allow_deferred_long_gop,
    )
    alignment_problem = _mp4_start_alignment_problem(
        tmp, source=path, probe_alignment=probe_alignment,
        probe_opening=probe_opening)
    if alignment_problem is not None:
        tmp.unlink(missing_ok=True)
        raise TranscodeFailedError(
            f"transcode output for {path.name} failed {alignment_problem}; "
            "original file preserved",
        )
    text_timing_problem = _mp4_text_timing_problem(
        tmp, source=path, probe_facts=probe_cues, probe_alignment=probe_alignment)
    if text_timing_problem is not None:
        tmp.unlink(missing_ok=True)
        raise TranscodeFailedError(
            f"transcode output for {path.name} failed {text_timing_problem}; "
            "original file preserved",
        )
    if not plan.needed:
        return
    outstanding = ", ".join(reason for reason, flag in (
        ("video", plan.needs_video), ("audio", plan.needs_audio),
        ("container", plan.needs_container), ("faststart", plan.needs_faststart),
        (f"{len(plan.added_audio)} audio track(s) still to add",
         bool(plan.added_audio)),
    ) if flag)
    tmp.unlink(missing_ok=True)
    raise TranscodeFailedError(
        f"transcode output for {path.name} still needs {outstanding}; "
        "original file preserved",
    )


def _finalize_transcode(
        path: Path, tmp: Path, target_suffix: str,
        replace=None) -> Path:
    """Install a verified output without clobbering a same-stem file."""
    replace = replace or replace_media_file
    new_path = path.with_suffix(target_suffix)
    if new_path != path and new_path.exists():
        new_path = unique_path(new_path)
    result_path = replace(tmp, path, new_path if new_path != path else None)
    if result_path != path:
        _move_sidecars_to_new_stem(path, result_path)
    return result_path


def _resolve_transcode_plan(
        path: Path, info: dict, plan: StreamingWorkPlan, *,
        container_only: bool, probe_audio) -> tuple[StreamingWorkPlan,
                                                    list[dict] | None] | None:
    """Narrow a plan and retain any audio streams obtained while doing so."""
    audio_streams = None
    resolved = plan
    if container_only:
        audio_streams = probe_audio(path)
        resolved = plan_container_only(plan, info, audio_streams)
        if resolved is None:
            return None
    if not (
        resolved.needs_video or resolved.needs_audio
        or resolved.needs_container or resolved.added_audio
    ):
        return None
    return resolved, audio_streams


def _with_start_alignment_facts(
        path: Path, info: dict, needs_video: bool, *, probe_alignment=None) -> dict:
    """Merge measured start-alignment facts into `info`, when they apply.

    c212: `_build_transcode_cmd` decides whether an output needs audio
    re-aligned to the video's first DTS by reading `video_first_pts`/
    `video_first_dts`/`audio_starts` off `info`; nothing populates those keys
    upstream (`_probe_video_audio` is a stable, widely-depended-on schema --
    see its own docstring -- so this stays a separate, additive probe rather
    than growing that one). Skipped when the video is being re-encoded: the
    output's own first DTS is not knowable yet (design.md step 3), so the
    probe would only cost an ffprobe call for a result `_build_transcode_cmd`
    is going to ignore anyway. The encoded MP4 is measured and realigned if
    needed after encoding, before the normal output verification.

    :param probe_alignment: Injectable seam for tests; defaults to the real
        `lib.mp4_start_alignment.probe_start_alignment`.
    :raises AlignmentProbeError: propagated uncaught when the probe read
        fails, so this file's transcode fails through the pipeline's
        existing per-file failure boundary instead of silently building the
        default (potentially misaligned) command: backfill's `_convert_one`
        (tools/backfill-streaming.py) catches, logs, and records it as this
        file's failure without stopping the batch; organize-media.py's
        `handle_folder` catches and logs it the same way per file. Never
        caught here -- the whole point is that this file's command is not
        built from an unread probe.
    """
    if needs_video:
        return info
    probe_alignment = probe_alignment or probe_start_alignment
    probe = probe_alignment(path)
    if probe is None:
        return info
    info = dict(info)
    info["video_first_pts"] = probe.video_first_pts
    info["video_first_dts"] = probe.video_first_dts
    info["audio_starts"] = probe.audio_starts
    return info


def _transcode_commands(
        path: Path, tmp: Path, info: dict, needs_video: bool,
        needs_audio: bool, *, target_suffix: str,
        added_audio, audio_streams: list[dict] | None,
        build_command=None, probe_alignment=None) -> tuple[list[str], list[str], list[str] | None]:
    """Build primary, subtitle-free, and optional software commands."""
    build_command = build_command or _build_transcode_cmd
    info = _with_start_alignment_facts(
        path, info, needs_video, probe_alignment=probe_alignment)
    shared = {
        "target_suffix": target_suffix, "added_audio": added_audio,
        "audio_streams": audio_streams,
    }
    command = build_command(
        path, tmp, info, needs_video, needs_audio, **shared,
    )
    no_subs = build_command(
        path, tmp, info, needs_video, needs_audio,
        include_subs=False, **shared,
    )
    software = None
    if "h264_videotoolbox" in command:
        software = build_command(
            path, tmp, info, needs_video, needs_audio,
            use_hardware=False, **shared,
        )
    return command, no_subs, software


def _realign_encoded_output(tmp: Path, *, probe=None, run=None) -> None:
    """Correct measured post-encode drift before the normal output checks."""
    measured = (probe or probe_start_alignment)(tmp)
    if measured is None:
        raise TranscodeFailedError(f"cannot measure video alignment for {tmp.name}")
    plan = plan_audio_alignment(
        measured.video_first_pts, measured.video_first_dts, measured.audio_starts)
    if plan.unmeasured:
        raise TranscodeFailedError(f"cannot measure audio alignment for {tmp.name}")
    if not plan.needed:
        return

    descriptor, name = tempfile.mkstemp(
        prefix=f".{tmp.stem}.realign.", suffix=".mp4", dir=tmp.parent)
    os.close(descriptor)
    corrected = Path(name)
    try:
        command = _build_realign_cmd(tmp, corrected, plan)
        result = (run or run_encode)(
            command, timeout=stream_copy_outer_timeout_seconds(tmp.stat().st_size),
            progress_path=corrected, extend_while_progressing=True)
        if result.returncode != 0 or not corrected.is_file() or corrected.stat().st_size == 0:
            raise TranscodeFailedError(
                f"audio realignment failed for {tmp.name}: rc={result.returncode}; "
                f"{_ffmpeg_stderr_tail(result)}")
        os.chmod(corrected, stat.S_IMODE(tmp.stat().st_mode))
        os.replace(corrected, tmp)
    finally:
        corrected.unlink(missing_ok=True)


def _execute_transcode(
        path: Path, tmp: Path, target_suffix: str, sidecars: list[Path],
        command: list[str], no_subs_command: list[str],
        software_command: list[str] | None, *, container_only: bool,
        run_transcode=None, verify=None, finalize=None,
        video_encoded: bool = False, realign=None,
        discard=None) -> Path:
    """Run, verify, and finalize one conversion fail-safely."""
    run_transcode = run_transcode or _run_transcode
    verify = verify or _verify_transcode_output
    finalize = finalize or _finalize_transcode
    discard = discard or discard_sidecars
    try:
        if not run_transcode(
                path, tmp, command, no_subs_command, software_command):
            raise TranscodeFailedError(
                f"ffmpeg failed for {path.name}; original file preserved",
            )
        if video_encoded and target_suffix == ".mp4":
            (realign or _realign_encoded_output)(tmp)
        verify(path, tmp, allow_deferred_long_gop=container_only)
        return finalize(path, tmp, target_suffix)
    except TranscodeFailedError:
        discard(sidecars)
        raise
    except EncodeCancelled:
        # The run's own stop request, not a transcode error: keep its
        # identity so callers do not count it as a failure strike.
        tmp.unlink(missing_ok=True)
        discard(sidecars)
        raise
    except Exception as exc:
        log.warning("transcode error for %s: %s", path.name, exc)
        tmp.unlink(missing_ok=True)
        discard(sidecars)
        raise TranscodeFailedError(
            f"transcode error for {path.name}: {exc}",
        ) from exc
