#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Plan and measure MP4 audio start-time alignment (c212).

ffmpeg's mp4 muxer starts every track at DTS 0 and expresses any other start
as an edit list (elst). A player that honours the edit list sees the right
sync; a player that ignores it (the Panasonic TX-LF37E30 target this task
exists for) sees the raw, un-corrected packet timeline instead, and any
stream-copied audio track that does not start at the video's own first
decode time plays back shifted by the gap.

This module is the single source of truth for that correction, used both to
DECIDE whether an mp4-writing ffmpeg command needs it (tools/lib/
transcode_command_plan.py's ``_build_transcode_cmd``) and to VERIFY that a
produced output actually achieved it (tools/lib/transcode_execution.py's
``_verify_transcode_output``, and tools/realign-mp4-start.py's remediation
pass). See deliverables/t_260905_091549_465/design.md for the full decision
and deliverables/t_260905_091549_465/editlist-dossier.md for the measurements
it is based on.

``plan_audio_alignment`` is pure: it takes already-measured numbers and
returns what to do. ``probe_start_alignment`` is the one small piece of I/O
that measures those numbers from a real file.

The writer's origin (c294; measured in tests/test_mp4_start_alignment_e2e.py,
WriterTimelineShapeTests).
Under ``-use_editlist 0`` the mp4 muxer switches on
``avoid_negative_ts=make_zero`` and applies ONE offset to every stream, taken
from the earliest DTS it sees; it then forces the first sample of every track
to DTS 0 and stretches that sample instead. A track that starts later than the
earliest one therefore keeps every sample in place except its first, which is
dragged back to the origin. So an aligned output is only correct when every
video and audio track starts on the SAME DTS. This module makes that true by
construction: the writer stays on the source's own timeline (``-copyts``),
the video's first DTS is measured on that exact timeline by running the
writer's own demux path (``_probe_video_first_packets``), every re-encoded
audio track's first packet is pinned to that DTS (``audio_anchor_bsf``), and
subtitle/data packets are clamped so none of them can start earlier
(``origin_clamp_bsf``). ``first_packet_gap_problem`` is the output check that
catches any writer that gets this wrong.
"""

from __future__ import annotations

import json
import re
import subprocess
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

from lib.encode_cancellation import run_cancellable_capture
from lib.encode_priority import run_encode
from lib.load_scaled_timeout import load_scaled_timeout
from lib.media_tools import require_tool

#: The survey threshold (editlist-dossier.md Part C): typical library files
#: sit at about 0.080s (a 2-frame composition delay) and the owner's
#: Panasonic fixture at that floor was reported in sync, so anything below
#: this is left alone -- default muxing, copied audio, no behaviour change.
ALIGN_THRESHOLD_SECONDS = 0.100


class AlignmentProbeError(Exception):
    """A start-alignment read on `path` failed; never returned as a plain None.

    Before this type existed, every probe read failure (a missing ffmpeg/
    ffprobe binary, a process that could not run or timed out, a nonzero
    exit, or output that could not be parsed) returned None, the same value
    used for the genuinely unrelated "this file needs no correction" case.
    On a contended host a transient timeout then read as "nothing to do":
    ``tools/realign-mp4-start.py`` reported "no change" for a file that in
    fact needed a +24.970s shift, and the streaming pipeline silently wrote
    an unaligned output. Raising this instead makes every caller either
    convert it to its own failure type or propagate it, so a transient read
    failure is retried, never mistaken for success.

    :ivar path: The file that could not be probed.
    :ivar detail: The underlying failure, as text: an exception's own
        ``str()``, or ``"rc=<n>: <stderr tail>"`` for a nonzero exit.
    """

    def __init__(self, path: Path, detail: str):
        super().__init__(f"cannot read start-alignment facts for {path.name}: {detail}")
        self.path = path
        self.detail = detail


@dataclass(frozen=True)
class AudioStreamStart:
    """One audio stream's measured start time and codec facts.

    :ivar index: Zero-based position among the file's audio streams (i.e.
        the ``n`` in ``0:a:n`` / ``-c:a:n``), not the global ffprobe stream
        index.
    :ivar start_time: The stream's own (edit-list-aware) start time in
        seconds, or None when ffprobe could not report one.
    :ivar codec: Lowercase short codec name (e.g. ``"aac"``, ``"ac3"``), or
        None when unknown.
    :ivar channels: Channel count, or None when unknown.
    :ivar bit_rate: Stream bit rate in bits per second, or None when ffprobe
        did not report one (common for a copied/compressed track whose
        per-stream rate is not stored in the container).
    :ivar sample_rate: Stream sample rate in Hz, or None when ffprobe did not
        report one. Used only to convert an ac3/eac3 encoder's priming delay
        (see ``_PRIMING_SAMPLES``) from samples to seconds; aac's own output
        rate is always 48000 regardless of this value (see
        ``audio_alignment_ffmpeg_args``).
    :ivar stream_index: The file-wide ffprobe stream index (the ``n`` in
        ``0:n``), or None when unknown. An added companion track names its
        source this way, so this is how it finds that source's shift.
    """

    index: int
    start_time: float | None
    codec: str | None
    channels: int | None
    bit_rate: int | None
    sample_rate: int | None = None
    stream_index: int | None = None


@dataclass(frozen=True)
class StartAlignmentProbe:
    """Everything :func:`plan_audio_alignment` needs, measured from a file.

    :ivar video_first_pts: The minimum presentation timestamp among the
        first packets of ``v:0``, in seconds.
    :ivar video_first_dts: The first packet's decode timestamp, in seconds.
    :ivar audio_starts: One entry per audio stream, in stream order.
    """

    video_first_pts: float
    video_first_dts: float
    audio_starts: tuple[AudioStreamStart, ...]


@dataclass(frozen=True)
class AudioAlignment:
    """One audio stream's planned shift relative to the video's first DTS."""

    stream: AudioStreamStart
    shift: float


@dataclass(frozen=True)
class AlignmentPlan:
    """Per-audio-stream shifts, and whether the output should be corrected.

    :ivar alignments: One entry per audio stream whose start was measured.
    :ivar needed: True when some shift reaches ``ALIGN_THRESHOLD_SECONDS``
        and every audio stream could be measured.
    :ivar anchor_dts: The video's first DTS on the writer's own timeline, in
        seconds: the one DTS every video and audio track of the output must
        start on (see the module docstring).
    :ivar unmeasured: ``index`` of every audio stream with no measurable
        start. Such a stream cannot be anchored, so its presence makes the
        plan unrealizable and ``needed`` False.
    """

    alignments: tuple[AudioAlignment, ...]
    needed: bool
    anchor_dts: float = 0.0
    unmeasured: tuple[int, ...] = ()


def plan_audio_alignment(video_first_pts: float, video_first_dts: float,
                         audio_starts) -> AlignmentPlan:
    """Return each audio stream's shift relative to the video's first DTS.

    ``shift = audio_start - video_first_dts``: the survey's ``av_shift_s``
    metric (editlist-dossier.md, "What the dossier shows"), computed here on
    whichever timeline the caller measured (a real source's own applied
    timeline, or a produced output's raw packet timeline). A stream whose
    start could not be measured is skipped rather than treated as zero,
    since silently assuming perfect alignment for an unmeasurable stream
    would hide the exact failure this plan exists to catch. It is recorded
    in ``unmeasured`` and makes the plan not needed: the output would carry
    that stream re-encoded with no anchor, and it would become the muxer's
    origin and drag every other track's first sample (c294).

    ``video_first_pts`` is not used in the shift formula: the reference is
    always the video's DECODE time, never its presentation time, so an
    ordinary B-frame composition delay (which moves pts, not dts) is never
    mistaken for a real start offset. It is accepted here (rather than only
    by callers) so every caller passes the same pair of numbers it measured,
    and a future consumer that does need it (e.g. reporting the composition
    offset alongside the plan) does not have to re-plumb it through.

    :param video_first_pts: The output's video min presentation time, seconds.
    :param video_first_dts: The output's video first decode time, seconds.
    :param audio_starts: Every audio stream's measured start/codec facts.
    :returns: The per-stream shifts, the anchor, and whether to correct.
    """
    del video_first_pts  # see docstring: kept in the signature, unused here
    audio_starts = tuple(audio_starts)
    alignments = tuple(
        AudioAlignment(stream=stream, shift=stream.start_time - video_first_dts)
        for stream in audio_starts
        if stream.start_time is not None
    )
    unmeasured = tuple(
        stream.index for stream in audio_starts if stream.start_time is None)
    needed = not unmeasured and any(
        abs(alignment.shift) >= ALIGN_THRESHOLD_SECONDS for alignment in alignments)
    return AlignmentPlan(alignments=alignments, needed=needed,
                         anchor_dts=video_first_dts, unmeasured=unmeasured)


def _format_bitrate(bit_rate: int | None, fallback: str) -> str:
    """Render a probed bits-per-second rate in the codebase's ``"NNNk"`` style."""
    if not bit_rate:
        return fallback
    return f"{bit_rate // 1000}k"


#: Encoder priming (algorithmic encode delay), in SAMPLES at the stream's own
#: OUTPUT sample rate. Measured directly, never guessed (deliverables/
#: t_260905_091549_465/): build a 10ms burst at a known time with no shift
#: applied, encode with the target codec, decode the RAW (``-ignore_editlist
#: 1``) timeline, and compare the measured onset to the un-encoded source's
#: own onset (same sample-accurate onset-detection method used by
#: tests.test_mp4_start_alignment_e2e's EncoderPrimingMeasurementTests).
#: Confirmed
#: identical in SAMPLES at 48000/44100/32000 Hz output rates (i.e. this is a
#: sample count, not a fixed duration): aac 1024 samples (matches the value
#: already used elsewhere in this codebase, and is the only one of the three
#: whose ``ffmpeg -h encoder=...`` capabilities line reports "delay"); ac3
#: and eac3 both 256 samples (one MDCT sub-block's worth of algorithmic
#: delay; ffmpeg's ac3/eac3 encoders report neither via initial_padding, so
#: unlike aac's this was never hidden behind an edit list even under the
#: muxer's default use_editlist=auto -- it is present in a plain, unshifted
#: encode's raw timeline exactly like aac's is once its own elst is
#: stripped, and this table corrects it exactly the same way).
_PRIMING_SAMPLES = {"aac": 1024, "ac3": 256, "eac3": 256}


def _priming_seconds(output_codec: str, output_sample_rate: int | None) -> float:
    """Encoder priming delay in seconds for one stream's actual OUTPUT codec.

    :param output_codec: The codec this stream is actually being encoded to
        (never the source codec when it differs -- e.g. an "mp3" source
        re-encoded to "aac" must prime as "aac", not "mp3").
    :param output_sample_rate: The sample rate that encode runs at. None
        (unprobed) falls back to 48000, this codebase's usual default for an
        unmeasured rate (e.g. ``lib.transcode_audio_plan``'s synthesized
        ``"48000"`` dict).
    :returns: 0.0 for a codec with no known priming.
    """
    samples = _PRIMING_SAMPLES.get(output_codec, 0)
    if not samples:
        return 0.0
    return samples / (output_sample_rate or 48000)


def _timeline_ticks(seconds: float) -> str:
    """An av_expr term for `seconds` in the packet's own time base."""
    return f"round({seconds!r}/TB)"


def audio_anchor_bsf(anchor_dts: float) -> str:
    """The ``setts`` bitstream filter that starts an encoded track on the anchor.

    A fresh encode's first packet sits wherever the encoder put it (aac:
    one priming frame before the filter output's 0; ac3: at 0), which is
    never the copied video's first DTS. Rebasing both PTS and DTS to the
    track's own start and adding the anchor puts the first packet exactly on
    the video's first DTS, so the muxer's one shared offset moves both to 0
    and neither first sample is dragged.

    :param anchor_dts: ``AlignmentPlan.anchor_dts``, seconds on the writer's
        timeline.
    :returns: A ``-bsf:a:N`` value.
    """
    ticks = _timeline_ticks(anchor_dts)
    return f"setts=pts=PTS-STARTPTS+{ticks}:dts=DTS-STARTDTS+{ticks}"


def origin_clamp_bsf(anchor_dts: float) -> str:
    """The ``setts`` filter that keeps a copied sparse stream off the origin.

    A subtitle cue or data packet timed before the video's first DTS would
    become the earliest packet, and so the muxer's origin, dragging the
    video's and audio's first samples. Clamping such packets to the anchor
    shows an early cue from the first frame on instead; later packets are
    untouched.

    :param anchor_dts: ``AlignmentPlan.anchor_dts``, seconds on the writer's
        timeline.
    :returns: A ``-bsf:s`` / ``-bsf:d`` value.
    """
    ticks = _timeline_ticks(anchor_dts)
    return f"setts=pts=max(PTS\\,{ticks}):dts=max(DTS\\,{ticks})"


def alignment_container_args(plan: AlignmentPlan) -> list[str]:
    """Output-wide args every aligned mp4 write needs, beside the audio args.

    ``-copyts`` keeps the writer on the demuxer's own timeline, the one
    ``plan.anchor_dts`` was measured on; subtitle and data packets are
    clamped to the anchor; ``-use_editlist 0`` drops the edit list the TV
    ignores. A stream spec that matches no stream (no data stream) is
    ignored by ffmpeg.

    :param plan: A needed :class:`AlignmentPlan`.
    :returns: Flat ffmpeg argv fragment.
    """
    clamp = origin_clamp_bsf(plan.anchor_dts)
    return ["-copyts", "-bsf:s", clamp, "-bsf:d", clamp, "-use_editlist", "0"]


def _shift_filter(shift: float, output_codec: str,
                  output_sample_rate: int | None) -> str | None:
    """The pad/trim filter chain realizing one shift net of encoder priming."""
    effective = shift - _priming_seconds(output_codec, output_sample_rate)
    if effective > 0:
        return f"asetpts=PTS-STARTPTS,adelay={round(effective * 1000)}:all=1"
    if effective < 0:
        return f"atrim=start={-effective},asetpts=PTS-STARTPTS"
    return None


def alignment_filter_for_source(plan: AlignmentPlan, stream_index: int, *,
                                output_codec: str = "aac",
                                output_sample_rate: int | None = 48000) -> str | None:
    """The shift filter for a NEW track encoded from one planned source stream.

    Used for an added companion track (an aac stereo encode of a source
    stream): it needs its source's shift, net of its own codec's priming,
    exactly like the source's own re-encode.

    :param plan: A needed :class:`AlignmentPlan`.
    :param stream_index: The source's file-wide index
        (``AudioStreamStart.stream_index``).
    :raises KeyError: when no planned stream has that index; a caller that
        cannot find its source must not realize the plan at all.
    :returns: The filter chain, or None when no shift remains.
    """
    for alignment in plan.alignments:
        if alignment.stream.stream_index == stream_index:
            return _shift_filter(alignment.shift, output_codec, output_sample_rate)
    raise KeyError(stream_index)


def audio_alignment_ffmpeg_args(plan: AlignmentPlan) -> list[str]:
    """Per-stream codec/filter args that realize an alignment plan.

    Design decision (design.md "Decision", step 2): when any stream needs
    correcting, EVERY audio stream in the output is re-encoded, not only the
    offending one -- under ``-use_editlist 0`` a copied sub-threshold stream
    gets the forced-first-packet artifact (editlist-dossier.md B.4), so a
    partially-corrected output would trade one sync bug for another.

    Codec choice preserves the surround-passthrough policy: ac3 stays ac3,
    eac3 stays eac3 (at the source bit rate when probed, else 640k); every
    other codec is re-encoded to aac at 48000 Hz, 192k for <=2 channels or
    384k for more (the builder's existing bitrate policy,
    ``tools/lib/transcode_command_plan.py``).

    Every output codec here (aac, ac3, eac3) has its own nonzero encoder
    priming/algorithmic delay (see ``_PRIMING_SAMPLES``): under
    ``-use_editlist 0`` there is no edit list left to hide it, so it would
    otherwise land in the output as extra, uncorrected silence in front of
    the real content -- measured directly on a real library file as AAC
    audio arriving about 21ms LATE (deliverables/t_260905_091549_465/,
    "DEFECT"). Every stream's shift is therefore adjusted by that priming
    before it is realized as a filter: ``effective = shift -
    priming(output_codec, output_sample_rate)``. A positive effective still
    pads with adelay; a negative one still trims with atrim; the only change
    from a plain ``shift`` is which exact amount gets padded/trimmed.

    Every stream's first packet is pinned to ``plan.anchor_dts`` with
    :func:`audio_anchor_bsf` (c294): the priming arithmetic above assumes the
    first packet sits on the video's first DTS, and only an anchored packet
    does.

    The caller is responsible for also adding
    :func:`alignment_container_args` to the command once, when
    ``plan.needed``; this function only emits the per-``a:N`` arguments.

    :param plan: An :class:`AlignmentPlan` with ``needed`` True.
    :returns: Flat ffmpeg argv fragment, in stream order.
    """
    args: list[str] = []
    for alignment in plan.alignments:
        n = alignment.stream.index
        codec = (alignment.stream.codec or "").lower()
        if codec in ("ac3", "eac3"):
            output_codec = codec
            output_sample_rate = alignment.stream.sample_rate
            args += [f"-c:a:{n}", codec,
                     f"-b:a:{n}", _format_bitrate(alignment.stream.bit_rate, "640k")]
        else:
            output_codec = "aac"
            output_sample_rate = 48000
            channels = alignment.stream.channels or 0
            bitrate = "384k" if channels > 2 else "192k"
            args += [f"-c:a:{n}", "aac", f"-ar:a:{n}", "48000",
                     f"-b:a:{n}", bitrate]
        chain = _shift_filter(alignment.shift, output_codec, output_sample_rate)
        if chain is not None:
            args += [f"-filter:a:{n}", chain]
        args += [f"-bsf:a:{n}", audio_anchor_bsf(plan.anchor_dts)]
    return args


def _parse_float(value) -> float | None:
    """Best-effort float parse for an ffprobe field that may be "N/A" or absent."""
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _parse_int(value) -> int | None:
    """Best-effort int parse for an ffprobe field that may be "N/A" or absent."""
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


#: The base (unscaled) bound for one ffprobe/ffmpeg read in this module;
#: scaled per call by :func:`load_scaled_timeout` (never memoized, since
#: load changes second to second -- see that function's own docstring).
_PROBE_BASE_TIMEOUT_SECONDS = 120


def _run_ffprobe(path: Path, args: list[str], *, timeout: float | None = None) -> dict:
    """Run one read-only ffprobe query and return its parsed JSON.

    :param timeout: Override for tests; defaults to a load-scaled bound.
    :raises AlignmentProbeError: the process could not be run or timed out,
        it exited nonzero, or its stdout was not valid JSON. A file with no
        stream matching the selector still succeeds here with an empty
        ``"streams"`` list -- that is a genuine result, never this exception
        (see :class:`AlignmentProbeError`).
    :raises MediaToolMissingError: ffprobe cannot be resolved.
    """
    ffprobe = require_tool("ffprobe")
    if timeout is None:
        timeout = load_scaled_timeout(_PROBE_BASE_TIMEOUT_SECONDS)
    try:
        result = run_cancellable_capture(
            [ffprobe, "-v", "error", *args, "-of", "json", str(path)],
            timeout=timeout,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise AlignmentProbeError(path, str(exc)) from exc
    if result.returncode != 0:
        raise AlignmentProbeError(
            path, f"ffprobe rc={result.returncode}: {(result.stderr or '')[-500:]}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise AlignmentProbeError(
            path, f"ffprobe produced unparseable output: {exc}") from exc


_FRAMECRC_TB = re.compile(r"^#tb 0: (\d+)/(\d+)$")


def _probe_video_first_packets(path: Path, *, timeout: float | None = None) -> tuple[float, float]:
    """video_first_pts (min) / video_first_dts (first) from v:0's opening packets.

    Measured by running the WRITER's own demux path, not ffprobe: a stream
    copy of the first 8 packets under ``-copyts`` into the framecrc muxer,
    which prints each packet's DTS and PTS exactly as an aligned write's
    muxer receives them. ffprobe cannot stand in for this: Matroska stores
    no DTS, so ffprobe reports N/A for a B-frame video's opening packets,
    while ffmpeg synthesizes one (measured: first packet pts 0.221, dts
    0.138) and that synthesized value is the one the output starts on.
    Reads only those 8 packets, never the whole file.

    :param timeout: Override for tests (e.g. an implausibly small value to
        force a genuine `subprocess.TimeoutExpired` without mocking);
        defaults to a load-scaled bound.
    :raises AlignmentProbeError: the process could not be run or timed out,
        it exited nonzero, or no packets or time base could be parsed from
        its output.
    :raises MediaToolMissingError: ffmpeg cannot be resolved.
    """
    ffmpeg = require_tool("ffmpeg")
    if timeout is None:
        timeout = load_scaled_timeout(_PROBE_BASE_TIMEOUT_SECONDS)
    try:
        # A media-touching ffmpeg launch, so it goes through run_encode
        # (encode_entry_point guard) rather than a bare subprocess.run --
        # not progress_report=True, though: that mode's liveness signal
        # (ffmpeg_progress_report's docstring) is only sound for DENSE
        # output, an audio stream decoded end to end, and this reads only
        # 8 packets total. A healthy run already finishes near-instantly
        # (nothing to protect from a flat bound), while a genuinely wedged
        # one would show zero growth from the start, so extend_while_
        # progressing's stall_seconds (900s default) would only turn a
        # ~120s-scaled fail-fast probe failure into a far slower one with
        # no compensating benefit. The load-scaled timeout already covers
        # this host's measured cold-read latency (up to ~66s, see
        # media_probe._probe_video_audio's comment) without that tradeoff.
        result = run_encode(
            [ffmpeg, "-nostdin", "-v", "error", "-copyts", "-i", str(path),
             "-map", "0:v:0", "-c", "copy", "-frames:v", "8",
             "-avoid_negative_ts", "disabled", "-f", "framecrc", "-"],
            timeout=timeout, text=True,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise AlignmentProbeError(path, str(exc)) from exc
    if result.returncode != 0:
        raise AlignmentProbeError(
            path, f"ffmpeg rc={result.returncode}: {(result.stderr or '')[-500:]}")
    time_base = None
    stamps: list[tuple[int, int]] = []
    for line in result.stdout.splitlines():
        match = _FRAMECRC_TB.match(line.strip())
        if match:
            time_base = Fraction(int(match.group(1)), int(match.group(2)))
            continue
        fields = [field.strip() for field in line.split(",")]
        if line.startswith("#") or len(fields) < 3 or fields[0] != "0":
            continue
        dts, pts = _parse_int(fields[1]), _parse_int(fields[2])
        if dts is not None and pts is not None:
            stamps.append((dts, pts))
    if time_base is None or not stamps:
        raise AlignmentProbeError(
            path, "no packets or time base parsed from the opening framecrc read")
    return (float(min(pts for _, pts in stamps) * time_base),
            float(stamps[0][0] * time_base))


def _probe_audio_starts(path: Path) -> tuple[AudioStreamStart, ...]:
    """Every audio stream's own start_time, codec, channels, bit_rate and
    sample_rate. A file with no audio stream returns an empty tuple; that is
    a real result parsed from a successful read, never a swallowed failure
    (see :func:`_run_ffprobe`, which raises instead of returning one)."""
    data = _run_ffprobe(path, [
        "-select_streams", "a",
        "-show_entries",
        "stream=index,codec_name,channels,bit_rate,sample_rate,start_time",
    ])
    return tuple(
        AudioStreamStart(
            index=position,
            start_time=_parse_float(stream.get("start_time")),
            codec=stream.get("codec_name"),
            channels=_parse_int(stream.get("channels")),
            bit_rate=_parse_int(stream.get("bit_rate")),
            sample_rate=_parse_int(stream.get("sample_rate")),
            stream_index=_parse_int(stream.get("index")),
        )
        for position, stream in enumerate(data.get("streams") or [])
    )


def probe_start_alignment(path: Path) -> StartAlignmentProbe | None:
    """Measure the facts :func:`plan_audio_alignment` needs from a real file.

    For an MP4 input, ffmpeg's demuxer applies the input's own edit list by
    default, so each stream's ``start_time`` here reflects the source's own
    intended (e.g. browser) timeline, not the raw packet timeline -- that is
    what lets this be used against both a pre-existing source (planning a
    correction) and a freshly produced output (verifying one, where no edit
    list should remain once ``-use_editlist 0`` was applied).

    :param path: The media file to probe.
    :raises AlignmentProbeError: any underlying ffprobe/ffmpeg read fails.
        Never returned as None: a transient read failure must not be
        mistaken for "this file needs no correction" (see
        :class:`AlignmentProbeError`).
    :returns: None only when `path` has no video stream at all -- the one
        legitimate "not applicable" case, checked before any read that could
        otherwise fail. A file with no audio stream still returns a probe
        with an empty ``audio_starts``.
    """
    video_streams = _run_ffprobe(
        path, ["-select_streams", "v", "-show_entries", "stream=index"])
    if not video_streams.get("streams"):
        return None
    video_first_pts, video_first_dts = _probe_video_first_packets(path)
    return StartAlignmentProbe(
        video_first_pts=video_first_pts,
        video_first_dts=video_first_dts,
        audio_starts=_probe_audio_starts(path),
    )


#: A first gap may exceed the steady spacing by this fraction of it before
#: it counts as stretched (a whole extra frame is 1.0).
_STRETCH_TOLERANCE = 0.5
#: Absolute slack on top, so millisecond rounding in a 1/1000 time base
#: (Matroska-sourced tracks) never reads as a stretch.
_STRETCH_SLACK_SECONDS = 0.001


def _first_gap_excess(times: list[float]) -> tuple[float, float] | None:
    """(first gap minus steady gap, steady gap) of an ordered timestamp list.

    The steady gap is the median of every gap after the first. None when
    fewer than four timestamps were read or the steady gap is not positive.
    """
    gaps = [later - earlier for earlier, later in zip(times, times[1:])]
    if len(gaps) < 3:
        return None
    later_gaps = sorted(gaps[1:])
    steady = later_gaps[len(later_gaps) // 2]
    if steady <= 0:
        return None
    return gaps[0] - steady, steady


def opening_gap_allowance(source_packets) -> float:
    """How far the SOURCE's own first gap already exceeds its steady spacing.

    A variable-frame-rate source can open with a long first frame (measured:
    16 of 80 sampled library MP4s, every one a VFR WEBRip, open with a
    2-frame first gap), and a writer that copies or re-encodes it carries
    that gap through honestly. Both orders are read, because a B-frame
    source can show the gap in presentation order while its DTS runs even:
    the larger excess of the DTS sequence and of the sorted PTS is allowed.

    :param source_packets: The source stream's opening ``(pts, dts)`` pairs,
        seconds, decode order; either value may be None.
    :returns: Seconds of first-gap excess the output may inherit, >= 0.
    """
    allowance = 0.0
    for times in ([dts for _, dts in source_packets if dts is not None],
                  sorted(pts for pts, _ in source_packets if pts is not None)):
        measured = _first_gap_excess(times)
        if measured is not None:
            allowance = max(allowance, measured[0])
    return allowance


def first_packet_gap_problem(dts: list[float], *, allowance: float = 0.0) -> str | None:
    """Name a track whose first sample was stretched to reach the origin.

    The output-side check for the module docstring's failure: a muxer that
    drags only a track's first sample to DTS 0 leaves a first DTS gap far
    wider than the track's steady spacing (measured: video 0.000 then 0.188
    at 24 fps; audio 0.000 then 0.083 against 21 ms frames). It judges the
    raw decode timeline's opening packets, so it catches any writer.

    :param dts: The track's opening packet DTS values, seconds, decode order,
        raw (edit list ignored).
    :param allowance: The source's own first-gap excess for this track
        (:func:`opening_gap_allowance`); only a gap WIDER than the source's
        is the writer's doing.
    :returns: None when fewer than four packets were read or the first gap
        is in line; otherwise the gap, the steady spacing and the allowance.
    """
    measured = _first_gap_excess(dts)
    if measured is None:
        return None
    excess, steady = measured
    if excess <= allowance + steady * _STRETCH_TOLERANCE + _STRETCH_SLACK_SECONDS:
        return None
    return (f"first sample stretched: first DTS gap {excess + steady:.3f}s against "
            f"a steady {steady:.3f}s (source allows {allowance:.3f}s extra)")


def _opening_packets(path: Path, selector: str) -> list[tuple[float | None, float | None]]:
    """One stream's first 12 packets as (pts, dts) on the raw timeline.

    A selector matching no stream still succeeds with an empty packet list
    (a real result); a read failure raises instead (see :func:`_run_ffprobe`).
    """
    data = _run_ffprobe(path, [
        "-ignore_editlist", "1", "-select_streams", selector,
        "-read_intervals", "%+#12", "-show_entries", "packet=pts_time,dts_time",
    ])
    return [(_parse_float(packet.get("pts_time")), _parse_float(packet.get("dts_time")))
            for packet in data.get("packets") or []]


def probe_opening_packets(path: Path) -> dict[str, list[tuple[float | None, float | None]]]:
    """Raw opening (pts, dts) packets of ``v:0`` and of every audio stream.

    :param path: A source or an mp4 output (any container ffprobe reads).
    :raises AlignmentProbeError: any underlying ffprobe read fails.
    :returns: ``{"video stream 0": [...], "audio stream N": [...]}``.
    """
    data = _run_ffprobe(path, ["-select_streams", "a", "-show_entries", "stream=index"])
    opening = {"video stream 0": _opening_packets(path, "v:0")}
    for position, _ in enumerate(data.get("streams") or []):
        opening[f"audio stream {position}"] = _opening_packets(path, f"a:{position}")
    return opening
