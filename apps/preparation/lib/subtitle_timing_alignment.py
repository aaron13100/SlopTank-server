#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Language-agnostic subtitle cue timing comparison (pure logic, no I/O).

Diagnosed 2026-09-23 against The Pitt S01: non-English subtitle sidecars
were placed next to videos with no timing check against that video.
The.Pitt.S01E01.720p.AMZN.WEB-DL.x264-MIKE.ja.srt is byte-identical to the
1080p release's .ja.srt even though the two rips are different cuts, and
against the 720p file's own in-sync English sidecar every ja cue starts
3.548-3.641s early. Nothing mechanical caught this because nothing compared
cue timing across languages.

The check this module implements does not need to understand any language:
once a video's English cue starts are known to be in sync, every other
language sidecar's cue START times must land on those same English cue
starts. Other languages segment speech into different cue boundaries (merged
lines, dropped filler, split long lines), but a cue that exists still starts
when the underlying speech starts, so start-time correspondence survives
segmentation differences that end-time or full-text comparison would not.

Three failure shapes are distinguished, all detectable from timing alone:
  - a constant global offset (wrong episode/cut, or a fixed sync error)
  - a linear frame-rate scale (23.976 vs 24 vs 25 fps authoring mismatch)
  - no correspondence at all (unrelated file entirely)

This module has no file or subprocess I/O: it operates on lists of cue start
times (floats, seconds) and raw subtitle text already read into memory. The
I/O layer (extracting a reference track, reading sidecar files) lives in
tools/lib/subtitle_timing_reference.py.
"""

from __future__ import annotations

import re
import statistics
from bisect import bisect_left, bisect_right
from dataclasses import dataclass
from enum import Enum

# A cue-start comparison needs enough cues on both sides for a match fraction
# to mean anything; below this, a handful of coincidental matches (or misses)
# swing the fraction wildly. Chosen well below a typical ~20-40 minute
# episode's real cue count (see calibration doc: every real sidecar sampled
# has 1000+ cues) so it only bites on genuinely short/partial files.
MIN_CUES = 50

# IN_SYNC_MIN calibrated against real The Pitt data 2026-09-23 (table in
# deliverables/pitt-subs-20260923/timing-gate-calibration.md). Same-cut
# English sidecars measure 0.99-1.00. Independently segmented but correctly
# timed French sidecars measure 0.63-0.74 identity (0.67-0.75 at their best
# transform), because only the cues that start a new utterance coincide.
# The negative control, 111 sidecar-vs-other-episode pairs, never exceeds
# 0.297 identity or 0.331 at the best transform. 0.6 sits between the two
# populations with >=0.27 margin to the unrelated maximum.
IN_SYNC_MIN = 0.6

# A transform must beat the identity by this much of the candidate's cues to
# be applied: below it the gain is refinement noise of the offset histogram
# (real in-sync French sidecars measure identity and best within 0.03 of
# each other, e.g. S01E01 1080p fr 0.708 vs 0.729).
SHIFT_GAIN_MIN = 0.05

# Offsets smaller than this are within cue-timing rounding; never rewrite a
# file for them.
_MIN_SHIFT_SECONDS = 0.05

# Candidate scale factors: identity, plus every pairwise ratio of the three
# frame rates this pipeline's source material actually uses (23.976, 24, 25).
_CANDIDATE_SCALES: tuple[float, ...] = (
    1.0,
    25 / 23.976,
    23.976 / 25,
    24 / 23.976,
    23.976 / 24,
    25 / 24,
    24 / 25,
)

# Offset search: pairs beyond this are never the same speech event on any
# real sidecar (max known real-world sync error is a few seconds; 120s is a
# large margin against a mis-set chapter/intro offset).
_OFFSET_SEARCH_WINDOW_SECONDS = 120.0
_OFFSET_HISTOGRAM_BIN_SECONDS = 0.1

_SRT_TIME = r"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{3})"
_SRT_CUE_RE = re.compile(_SRT_TIME + r"\s*-->\s*" + _SRT_TIME)
_ASS_DIALOGUE_START_RE = re.compile(
    r"^Dialogue:\s*[^,]*,\s*(\d+):(\d{2}):(\d{2})\.(\d{2})\s*,", re.MULTILINE
)


class Verdict(Enum):
    """Outcome of comparing one sidecar's cue starts against a reference."""

    IN_SYNC = "in_sync"
    SHIFTED = "shifted"
    UNRELATED = "unrelated"
    INSUFFICIENT = "insufficient"


@dataclass(frozen=True)
class AlignmentResult:
    """Result of measure_alignment: how well candidate cue starts line up
    with reference cue starts, and under what transform.

    :param identity_match_frac: Fraction of candidate starts within
        tolerance of some reference start with no transform applied
        (scale=1, offset=0). This alone decides IN_SYNC.
    :param best_scale: Best-fitting scale factor found by the search.
    :param best_offset: Best-fitting offset (seconds) found by the search,
        for best_scale.
    :param best_match_frac: Fraction of candidate starts matched under
        (best_scale, best_offset).
    :param verdict: Overall classification (see Verdict).
    :param n_candidate: Number of candidate cue starts compared.
    :param n_reference: Number of reference cue starts compared.
    """

    identity_match_frac: float
    best_scale: float
    best_offset: float
    best_match_frac: float
    verdict: Verdict
    n_candidate: int
    n_reference: int


def cue_starts_from_text(text: str) -> list[float]:
    """Extract sorted cue start times (seconds) from SRT or ASS/SSA text.

    Recognizes SRT "HH:MM:SS,mmm --> HH:MM:SS,mmm" cue lines (both the
    standard comma decimal separator and a "." variant some tools emit),
    and ASS/SSA "Dialogue: ...,<start>,<end>,..." lines (start field in
    H:MM:SS.cc centisecond form). A file that matches neither pattern
    yields an empty list.

    :param text: Full subtitle file contents.
    :returns: Sorted list of cue start times in seconds.
    """
    starts: list[float] = []
    for h, m, s, ms, _eh, _em, _es, _ems in _SRT_CUE_RE.findall(text):
        starts.append(int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0)
    for h, m, s, cs in _ASS_DIALOGUE_START_RE.findall(text):
        starts.append(int(h) * 3600 + int(m) * 60 + int(s) + int(cs) / 100.0)
    starts.sort()
    return starts


def _match_frac(candidate: list[float], sorted_reference: list[float],
                 scale: float, offset: float, tolerance: float) -> float:
    """Fraction of candidate starts with some reference start within
    tolerance of scale*t + offset, using bisection on sorted_reference."""
    if not candidate:
        return 0.0
    matched = 0
    for t in candidate:
        target = scale * t + offset
        lo = bisect_left(sorted_reference, target - tolerance)
        hi = bisect_right(sorted_reference, target + tolerance)
        if hi > lo:
            matched += 1
    return matched / len(candidate)


def _best_offset_for_scale(candidate: list[float], sorted_reference: list[float],
                            scale: float, tolerance: float) -> float | None:
    """Modal-bin-then-median offset estimate for a fixed scale.

    Builds an offset histogram of (ref - scale*cand) over every
    (candidate, reference) pair within +-_OFFSET_SEARCH_WINDOW_SECONDS
    (bisect-bounded so this stays near-linear rather than O(n*m)), bins at
    _OFFSET_HISTOGRAM_BIN_SECONDS resolution, takes the modal bin, then
    refines to the median of the pair differences within +-tolerance of
    that bin's centre. Returns None when no pair fell within the window
    (nothing to estimate an offset from).
    """
    bin_width = _OFFSET_HISTOGRAM_BIN_SECONDS
    window = _OFFSET_SEARCH_WINDOW_SECONDS
    histogram: dict[int, list[float]] = {}
    for t in candidate:
        st = scale * t
        lo = bisect_left(sorted_reference, st - window)
        hi = bisect_right(sorted_reference, st + window)
        for r in sorted_reference[lo:hi]:
            diff = r - st
            bin_index = int(diff // bin_width)
            histogram.setdefault(bin_index, []).append(diff)
    if not histogram:
        return None
    modal_bin = max(histogram, key=lambda b: len(histogram[b]))
    bin_centre = (modal_bin + 0.5) * bin_width
    refine_pool = [d for diffs in histogram.values() for d in diffs
                   if abs(d - bin_centre) <= tolerance]
    if not refine_pool:
        refine_pool = histogram[modal_bin]
    return statistics.median(refine_pool)


def measure_alignment(candidate: list[float], reference: list[float], *,
                       tolerance: float = 0.3) -> AlignmentResult:
    """Compare candidate cue starts against a reference and classify.

    :param candidate: Cue start times (seconds) of the sidecar under test.
    :param reference: Cue start times (seconds) of the trusted reference
        (typically the video's own verified-in-sync English track).
    :param tolerance: Seconds within which a candidate start counts as
        matching a reference start.
    :returns: AlignmentResult with the identity match fraction, the best
        transform found by the scale/offset search, and a verdict.
    """
    n_candidate = len(candidate)
    n_reference = len(reference)
    if n_candidate < MIN_CUES or n_reference < MIN_CUES:
        identity = _match_frac(candidate, sorted(reference), 1.0, 0.0, tolerance) \
            if candidate and reference else 0.0
        return AlignmentResult(
            identity_match_frac=identity, best_scale=1.0, best_offset=0.0,
            best_match_frac=identity, verdict=Verdict.INSUFFICIENT,
            n_candidate=n_candidate, n_reference=n_reference,
        )

    sorted_reference = sorted(reference)
    identity_match_frac = _match_frac(candidate, sorted_reference, 1.0, 0.0, tolerance)

    best_scale = 1.0
    best_offset = 0.0
    best_match_frac = -1.0
    for scale in _CANDIDATE_SCALES:
        offset = _best_offset_for_scale(candidate, sorted_reference, scale, tolerance)
        if offset is None:
            continue
        frac = _match_frac(candidate, sorted_reference, scale, offset, tolerance)
        if frac > best_match_frac:
            best_scale, best_offset, best_match_frac = scale, offset, frac
    if best_match_frac < 0:
        best_match_frac = 0.0

    # A transform counts only if it is not the identity and it matches
    # meaningfully more cues than the identity does. Its offset magnitude is
    # irrelevant: a track lagging by less than `tolerance` still has starts
    # that do not land on the English starts (The.Pitt.S01E03.1080p fr:
    # identity 0.495, 0.672 at -0.208s), and it is retimed, never retired.
    transform_wins = (best_scale != 1.0 or abs(best_offset) >= _MIN_SHIFT_SECONDS) \
        and best_match_frac - identity_match_frac >= SHIFT_GAIN_MIN
    if best_match_frac >= IN_SYNC_MIN and transform_wins:
        verdict = Verdict.SHIFTED
    elif identity_match_frac >= IN_SYNC_MIN:
        verdict = Verdict.IN_SYNC
    else:
        verdict = Verdict.UNRELATED

    return AlignmentResult(
        identity_match_frac=identity_match_frac, best_scale=best_scale,
        best_offset=best_offset, best_match_frac=best_match_frac, verdict=verdict,
        n_candidate=n_candidate, n_reference=n_reference,
    )


def _format_srt_timestamp(seconds: float, sep: str) -> str:
    seconds = max(0.0, seconds)
    total_ms = round(seconds * 1000)
    ms = total_ms % 1000
    total_s = total_ms // 1000
    s = total_s % 60
    total_m = total_s // 60
    m = total_m % 60
    h = total_m // 60
    return f"{h:02d}:{m:02d}:{s:02d}{sep}{ms:03d}"


def apply_transform(srt_text: str, scale: float, offset: float) -> str:
    """Rewrite every SRT cue timestamp (start and end) under scale*t+offset.

    Negative results are clamped to 0. Everything else in the file
    (sequence numbers, text, blank lines, the "-->" separator and each
    timestamp's original decimal separator) is preserved byte-for-byte.

    :param srt_text: Original SRT file contents.
    :param scale: Multiplicative factor applied to every timestamp.
    :param offset: Additive offset (seconds) applied after scaling.
    :returns: Rewritten SRT text.
    """
    def _replace(match: re.Match) -> str:
        h1, m1, s1, ms1, h2, m2, s2, ms2 = match.groups()
        start = int(h1) * 3600 + int(m1) * 60 + int(s1) + int(ms1) / 1000.0
        end = int(h2) * 3600 + int(m2) * 60 + int(s2) + int(ms2) / 1000.0
        # Recover each timestamp's own decimal separator from the original
        # text so the rewrite does not normalize a "." variant to ",".
        span_text = match.group(0)
        first_sep = "," if "," in span_text.split("-->")[0] else "."
        second_sep = "," if "," in span_text.split("-->")[1] else "."
        new_start = scale * start + offset
        new_end = scale * end + offset
        return (f"{_format_srt_timestamp(new_start, first_sep)} --> "
                f"{_format_srt_timestamp(new_end, second_sep)}")

    return _SRT_CUE_RE.sub(_replace, srt_text)
