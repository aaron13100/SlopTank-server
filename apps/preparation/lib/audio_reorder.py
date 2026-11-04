#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Physically map the identified normal soundtrack as audio stream 0.

Distinct from merely fixing disposition flags, which leaves stream ORDER
unchanged -- see
deliverables/download-portable-media-design.md sections 2-3 for why a
first-stream-only player (a Google-hosted preview, this project's original
bug report) needs the physical order corrected too, not just the flag.
"""

from __future__ import annotations

import logging
import stat
from collections.abc import Callable
from pathlib import Path

from lib.audio_defaults import (
    _probe_audio_streams,
    _resolve_default_index,
    _disposition_value,
    _identify_normal_soundtrack,
    _is_browser_playable_audio,
    _is_tv_playable_audio,
)
from lib.media_identity import replace_media_file
from lib.media_probe import _ffmpeg_stderr_tail
from lib.media_write_lock import MediaWriteBusy, exclusive_media_write
from lib.movie_identity_match import normalize_title, expected_from_path
from lib.transcode import _stream_language, _is_commentary_track
from lib.encode_priority import run_encode
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")

# The five confirmed Riff-franchise titles (owner-confirmed by ear, per the
# original bug report), keyed by the same folder-derived (normalized title,
# year) identity movie_identity_match already uses to compare a file against
# its metadata -- never a bare title-string guess. Adding a title here is
# deliberately a code change, not a config toggle: it is exactly the "human
# confirms a suspicious file's audio by ear" process the design document
# describes, and it must stay reviewable the same way any other behavior
# change is.
_KNOWN_RIFF_FRANCHISE_TITLES = frozenset({
    (normalize_title("Twilight1"), 2008),
    (normalize_title("Twilight2 New Moon"), 2009),
    (normalize_title("Twilight3 Eclipse"), 2010),
    (normalize_title("Twilight4 Breaking Dawn"), 2011),
    (normalize_title("Twilight5 Breaking Dawn"), 2012),
})


def _is_known_riff_franchise(path: Path) -> bool:
    """True for a file whose folder identity is on the confirmed-franchise
    allowlist (see audio_defaults._identify_normal_soundtrack's
    known_riff_franchise parameter)."""
    title, year = expected_from_path(str(path))
    return (normalize_title(title), year) in _KNOWN_RIFF_FRANCHISE_TITLES


def _find_companions(streams: list[dict], container_suffix: str) -> set[int]:
    """Indexes of EXISTING streams that behave like a pipeline-added
    browser/TV-playable AAC stereo companion, across every (language,
    commentary) group.

    Deliberately NOT a bare codec-shape scan ("aac, 2ch, 48000"): a
    pre-existing alternate track (this project's real Twilight files carry
    two -- a Riff track and a plain stereo downmix, both aac/2ch/48000) can
    share that exact shape by coincidence without being a companion the
    pipeline added, and treating it as one would swap the physical/default
    winner right back to a track this whole function exists to demote. The
    real test is whether the GROUP would still be browser- and TV-playable
    WITHOUT this specific candidate: a genuine companion is the one track a
    group depends on for playability (removing it breaks the group), while a
    coincidental extra track is redundant (the group is fine without it, as
    Twilight's Riff and plain-downmix tracks both are, since each is already
    covered by the other). A single-member group is never a companion of
    itself (nothing to be redundant WITH), which is why groups of one are
    skipped outright.

    These indexes must be excluded before identity resolution runs (see
    reorder_preferred_audio), not merely paired in afterward: an added
    companion sitting inside an already-multi-track group creates exactly
    the shape audio_defaults._identify_normal_soundtrack treats as
    suspicious (a stereo/mono default-flagged candidate beside a
    higher-channel one AND another stereo/mono candidate), so leaving it in
    the identity-resolution input can flag an otherwise-unambiguous file as
    ambiguous purely because a companion was added to it.
    """
    groups: dict[tuple[str, bool], list[int]] = {}
    for i, s in enumerate(streams):
        key = (_stream_language(s), _is_commentary_track(s))
        groups.setdefault(key, []).append(i)
    companions: set[int] = set()
    for indexes in groups.values():
        if len(indexes) < 2:
            continue
        for i in indexes:
            s = streams[i]
            if not (str(s.get("codec_name") or "").strip().lower() == "aac"
                    and int(s.get("channels") or 0) == 2
                    and int(s.get("sample_rate") or 0) == 48000):
                continue
            rest = [streams[j] for j in indexes if j != i]
            satisfied_without = (
                any(_is_browser_playable_audio(r) for r in rest)
                and any(_is_tv_playable_audio(r, container_suffix) for r in rest)
            )
            if not satisfied_without:
                companions.add(i)
    return companions


def _build_reorder_cmd(path: Path, tmp: Path, streams: list[dict],
                       order: list[int]) -> list[str]:
    """ffmpeg argv that physically reorders the audio streams of ``path`` to
    ``order`` (a permutation of audio-relative indexes), copying every other
    stream (video, subtitle, data, attachments, chapters, metadata) unchanged.

    Disposition flags are written by OUTPUT position (enumerate(order)), not
    by the streams' ORIGINAL index -- see design section 3.1's implementation
    note: building them from the original probe order after the map order has
    already changed would silently attach stream 0's original flags to
    whatever now occupies output position 0.
    """
    ffmpeg = require_tool("ffmpeg")
    cmd = [ffmpeg, "-nostdin", "-y", "-i", str(path), "-map", "0:v"]
    for idx in order:
        cmd += ["-map", f"0:a:{idx}"]
    cmd += ["-map", "0:s?"]
    if path.suffix.lower() not in (".mp4", ".m4v"):
        cmd += ["-map", "0:t?"]
    cmd += ["-map", "0:d?", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy"]
    for pos, idx in enumerate(order):
        cmd += [f"-disposition:a:{pos}", _disposition_value(streams[idx], pos == 0)]
    # A standalone maintenance reorder has no later container-remux stage to
    # restore faststart. Without this, ffmpeg writes MP4's moov atom at the end
    # and the audio-order fix merely exchanges an HLS delay for a progressive
    # download that cannot start on the first bytes.
    if path.suffix.lower() in (".mp4", ".m4v"):
        cmd += ["-movflags", "+faststart"]
    cmd.append(str(tmp))
    return cmd


def _desired_order(streams: list[dict], container_suffix: str,
                   path: Path) -> list[int] | None:
    """The audio-relative order this file's streams should physically appear
    in, or None when the identity is unresolvable (unprobeable shape,
    ambiguous) or the file is already correct. See reorder_preferred_audio.
    """
    companions = _find_companions(streams, container_suffix)
    identity_candidate_indexes = [i for i in range(len(streams)) if i not in companions]
    identity_candidates = [streams[i] for i in identity_candidate_indexes]
    local_index, ambiguous = _identify_normal_soundtrack(
        identity_candidates, known_riff_franchise=_is_known_riff_franchise(path))
    if local_index is None or ambiguous:
        return None
    identity_index = identity_candidate_indexes[local_index]

    identity_key = (_stream_language(streams[identity_index]),
                    _is_commentary_track(streams[identity_index]))
    companion_index = next(
        (i for i in companions
         if (_stream_language(streams[i]), _is_commentary_track(streams[i])) == identity_key),
        None,
    )
    target_index = identity_index
    if companion_index is not None:
        resolved = _resolve_default_index(
            [streams[identity_index], streams[companion_index]], container_suffix)
        if resolved == 1:
            target_index = companion_index

    order = [target_index]
    if companion_index is not None:
        order.append(identity_index if target_index == companion_index else companion_index)
    order += [i for i in range(len(streams)) if i not in order]

    current_defaults = [i for i, s in enumerate(streams)
                        if (s.get("disposition") or {}).get("default")]
    if order == list(range(len(streams))) and current_defaults == [order[0]]:
        return None
    return order


def needs_preferred_audio_reorder(path: Path) -> bool:
    """Whether ``path`` needs the safe physical/default audio-order repair.

    This is the read-only selector used by existing-library maintenance.  It
    deliberately delegates to the same ``_desired_order`` function that does
    the rewrite: ambiguous identities and already-correct files both return
    False, so the scheduler cannot select a file the writer would have to
    guess about.

    :param path: Existing media file to inspect.
    :returns: True only when a deterministic reorder is currently required.
    """
    if not path.exists():
        return False
    streams = _probe_audio_streams(path)
    return len(streams) >= 2 and _desired_order(streams, path.suffix, path) is not None


def _publish_reordered_candidate(
    path: Path,
    tmp: Path,
    streams: list[dict],
    target_index: int,
    before_replace: Callable[[], bool | None] | None,
) -> bool:
    """Publish one complete candidate only when playback is definitely idle."""
    tmp.chmod(stat.S_IMODE(path.stat().st_mode))
    if before_replace is not None:
        try:
            replace_allowed = before_replace() is True
        except Exception:  # Playback-state uncertainty must fail closed.
            replace_allowed = False
        if not replace_allowed:
            log.warning(
                "audio reorder replacement refused by playback gate for %s; "
                "discarding output",
                path.name,
            )
            tmp.unlink(missing_ok=True)
            return False
    replace_media_file(tmp, path)
    chosen_title = (streams[target_index].get("tags") or {}).get("title")
    log.info("audio reorder corrected: %s -> %s now first/default",
             path.name, chosen_title or "preferred track")
    return True


def reorder_preferred_audio(
    path: Path,
    *,
    before_replace: Callable[[], bool | None] | None = None,
) -> bool:
    """Physically map the identified normal soundtrack (or its browser/TV
    companion, whichever should carry the default flag) as audio stream 0,
    the sole default, retaining every other stream. Copy-only remux; a no-op
    (no ffmpeg invocation) when the file is already correct.

    Stateless and idempotent by construction (design section 6): every call
    re-probes and re-derives the desired order from scratch (_desired_order),
    comparing it against the file's CURRENT physical order and default flags.
    Safe to call before a companion track exists (nothing to pair against, so
    the identity winner alone is placed first) and again after one has been
    added by the transcode pass (this time the companion is found and the
    pairing rule can move it forward if it should be default instead).

    Returns False (no-op) when the file is unprobeable, has fewer than two
    audio streams, the identity is ambiguous, or it is already correct.

    :raises MediaToolMissingError: ffmpeg cannot be resolved.
    """
    if not path.exists():
        return False
    require_tool("ffmpeg")
    streams = _probe_audio_streams(path)
    if len(streams) < 2:
        return False
    order = _desired_order(streams, path.suffix, path)
    if order is None:
        return False
    target_index = order[0]

    try:
        with exclusive_media_write(path, "audio-reorder") as tmp:
            try:
                result = run_encode(
                    _build_reorder_cmd(path, tmp, streams, order),
                    # Whole-file stream copies can remain healthy for hours while the
                    # production volume is I/O-throttled, so the progress watchdog
                    # (not a flat deadline) is the PRIMARY lifetime bound: it kills a
                    # write that stops, never a slow write that keeps advancing.
                    # timeout= (from this file's own size, see
                    # stream_copy_outer_timeout_seconds) exists because, per
                    # t_260919_232308_921, the watchdog's own poll thread can itself
                    # be wedged on the same stalled volume as ffmpeg, with nothing
                    # else bounding the encode's lifetime at all.
                    # extend_while_progressing=True (t_260920_052208_901): Darwin's
                    # background I/O tier can starve real throughput by a factor
                    # with no ceiling, so timeout= alone SIGKILLed a live, healthy
                    # multi-hour copy under load. With this set, the wait only ends
                    # once the watchdog's own cached progress goes stale for a full
                    # stall window, never merely because wall-clock time passed --
                    # see stream_copy_outer_timeout_seconds' docstring.
                    timeout=stream_copy_outer_timeout_seconds(path.stat().st_size),
                    progress_path=tmp,
                    extend_while_progressing=True,
                )
            except Exception as exc:
                log.warning("audio reorder failed for %s: %s", path.name, exc)
                tmp.unlink(missing_ok=True)
                return False
            if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size == 0:
                log.warning("audio reorder failed for %s: rc=%s; ffmpeg said: %s",
                            path.name, result.returncode, _ffmpeg_stderr_tail(result))
                tmp.unlink(missing_ok=True)
                return False
            return _publish_reordered_candidate(
                path, tmp, streams, target_index, before_replace)
    except MediaWriteBusy as exc:
        log.warning("audio reorder skipped for %s: %s", path.name, exc)
        return False
