#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Embed every finalized SRT sidecar into the container as a selectable text
subtitle track, then archive the now-redundant sidecar.

Runs LAST in the pipeline (after provider fetch + ffsubsync), so the embedded
track always reflects the final, synced subtitle -- see
deliverables/download-portable-media-design.md sections 5, 6, 8, 9.
"""

from __future__ import annotations

import glob as globlib
import logging
import re
import shutil
import stat
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from lib.encode_priority import run_encode
from lib.encode_watchdog import stream_copy_outer_timeout_seconds
from lib.media_identity import replace_media_file
from lib.media_probe import _probe_video_audio, _ffmpeg_stderr_tail
from lib.media_write_lock import MediaWriteBusy, exclusive_media_write
from lib.media_temp_housekeeping import SUBTITLE_TRANSIENT_MARKERS
from lib.organize_media_config import QUARANTINE_ROOT, MEDIA_ROOT
from lib.transcode import _TEXT_SUBTITLE_CODECS
from lib.transcode_command_plan import (
    MP4_TEXT_GAP_LIMIT_SECONDS,
    text_gap_overflow,
)
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")

# Transient markers written by in-progress pipeline steps; a sidecar still
# carrying one is mid-flight, never a finalized subtitle to embed.
_TRANSIENT_MARKERS = SUBTITLE_TRANSIENT_MARKERS

# Sidecar filenames use the 2-letter/BCP-47-ish codes this pipeline's fetch
# stages already write (PREFETCH_HASH_SUB_LANGS: "en,fr,es,de,ja,it,pt-BR,pl,
# nl,sv"), but embedded container subtitle tracks use ISO-639-2 (3-letter)
# codes -- confirmed directly: ffmpeg's MP4 muxer SILENTLY DROPS a
# "language=en" metadata tag entirely (probes back as no language at all)
# because "en" is not a valid ISO-639-2 value, while "language=eng" is written
# and read back correctly. Comparing "en" against an existing "eng" tag
# without this mapping means the staleness/dedup check can never find a match
# at all, on every real library file (every embedded language tag this
# project has ever observed is 3-letter, e.g. Twilight's "eng").
_LANG_ALIASES = {
    "en": "eng", "ja": "jpn", "fr": "fre", "es": "spa", "de": "ger",
    "it": "ita", "pt-br": "por", "pt": "por", "pl": "pol", "nl": "dut", "sv": "swe",
}


def _canonical_lang(code: str) -> str:
    """Map a sidecar-style language code to the ISO-639-2 form embedded
    subtitle tracks (and this pipeline's own newly-embedded tracks) use.
    Passes through anything already 3-letter or unmapped, lowercased."""
    code = (code or "").strip().lower()
    return _LANG_ALIASES.get(code, code)


def _finalized_srt_sidecars(video_path: Path) -> dict[str, Path]:
    """<stem>.<lang>.srt sidecars beside ``video_path``, keyed by lowercased
    language code. Only the exact <stem>.<lang>.srt shape this pipeline's own
    fetch/sync stages write; anything with an extra dot segment (a transient
    marker, or a name this pipeline did not produce) is skipped."""
    stem = video_path.stem
    result: dict[str, Path] = {}
    for sub in sorted(video_path.parent.glob(globlib.escape(stem) + ".*.srt")):
        if any(marker in sub.name for marker in _TRANSIENT_MARKERS):
            continue
        rest = sub.name[len(stem) + 1: -len(".srt")]
        if not rest or "." in rest:
            continue
        result[rest.strip().lower()] = sub
    return result


def _first_input_size_bytes(input_args: tuple[str, ...]) -> int:
    """Size of the first ``-i`` input in ``input_args``, 0 when unavailable.

    Feeds stream_copy_outer_timeout_seconds: the demux this bounds reads the
    whole input container, so the bound must scale with that container's
    size (i261, finding section 4). 0 -- absent flag or unstatable path --
    yields the function's floor, the safest bound for an unknown size.
    """
    for position, flag in enumerate(input_args):
        if flag == "-i" and position + 1 < len(input_args):
            try:
                return Path(input_args[position + 1]).stat().st_size
            except OSError:
                return 0
    return 0


def _normalize_srt_bytes(ffmpeg: str, *input_args: str) -> bytes | None:
    """Run ``input_args`` through ffmpeg's SRT muxer and return the bytes.

    Used to compare two subtitle sources (an embedded stream and a sidecar
    file) on equal footing: an MP4's mov_text track re-serializes differently
    from a raw .srt file, so comparing raw bytes on one side against ffmpeg's
    own SRT writer on the other would spuriously report every MP4 file as
    changed. Passing BOTH sides through the identical SRT writer cancels that
    out.
    """
    try:
        # Extracting one subtitle stream still demuxes the whole container, so
        # on a multi-gigabyte film this is a full read of the volume Jellyfin
        # streams from. text=False because the comparison this feeds is
        # byte-exact and must not be routed through a decode. The bound is
        # size-derived from the input because the output here is stdout: no
        # file exists for the stall watchdog to watch, so this flat bound is
        # the call's only one (i261; the old flat 60s could not cover a full
        # multi-GB read under the background-I/O tier).
        result = run_encode(
            [ffmpeg, "-v", "error", *input_args, "-f", "srt", "-"],
            timeout=stream_copy_outer_timeout_seconds(
                _first_input_size_bytes(input_args)),
            text=False,
        )
        if result.returncode != 0:
            return None
        return result.stdout
    except Exception:
        return None


# Cue timestamp line, e.g. "00:00:00,023 --> 00:00:00,923".
_SRT_TIME_RE = re.compile(
    r"(\d+):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d+):(\d{2}):(\d{2}),(\d{3})")

# A verified-real measurement (2026-09-06, this ffmpeg build): embedding a
# sidecar and immediately extracting it back shifts every cue by ~23ms --
# encoder/muxer priming delay, not a content change. An exact-byte or
# exact-timestamp comparison would call that "stale" and re-embed on EVERY
# run forever, which is worse than no marker at all (violates invariant 10).
# A genuine ffsubsync correction is orders of magnitude larger (it exists to
# fix noticeable drift, typically whole seconds), so this tolerance is what
# actually distinguishes "no real change" from "really re-synced."
_TIMING_TOLERANCE_MS = 1000


def _parse_srt_cues(data: bytes) -> list[tuple[int, int, str]]:
    """[(start_ms, end_ms, cue_text), ...] from raw SRT bytes, in order."""
    text = data.decode("utf-8", "replace")
    cues: list[tuple[int, int, str]] = []
    for block in re.split(r"\n\s*\n", text.strip()):
        lines = block.splitlines()
        for i, line in enumerate(lines):
            m = _SRT_TIME_RE.search(line)
            if not m:
                continue
            h1, m1, s1, ms1, h2, m2, s2, ms2 = (int(g) for g in m.groups())
            start = ((h1 * 60 + m1) * 60 + s1) * 1000 + ms1
            end = ((h2 * 60 + m2) * 60 + s2) * 1000 + ms2
            cues.append((start, end, "\n".join(lines[i + 1:]).strip()))
            break
    return cues


def _cues_match(a: list[tuple[int, int, str]], b: list[tuple[int, int, str]]) -> bool:
    """True when both cue lists have the same text in the same order, and
    every timing pair is within _TIMING_TOLERANCE_MS of each other."""
    if len(a) != len(b):
        return False
    return all(
        text_a == text_b
        and abs(start_a - start_b) <= _TIMING_TOLERANCE_MS
        and abs(end_a - end_b) <= _TIMING_TOLERANCE_MS
        for (start_a, end_a, text_a), (start_b, end_b, text_b) in zip(a, b)
    )


def _content_matches(ffmpeg: str, video_path: Path, stream_index: int,
                     sidecar: Path) -> bool:
    """True when the currently-embedded subtitle stream and the sidecar file
    carry the same cues, within the muxing-noise timing tolerance (see
    _TIMING_TOLERANCE_MS). A mismatch means either the text actually changed
    or a real ffsubsync correction landed since the last embed."""
    embedded = _normalize_srt_bytes(ffmpeg, "-i", str(video_path), "-map", f"0:{stream_index}")
    current = _normalize_srt_bytes(ffmpeg, "-i", str(sidecar))
    if embedded is None or current is None:
        return False
    return _cues_match(_parse_srt_cues(embedded), _parse_srt_cues(current))


@dataclass(frozen=True)
class ArchiveRoots:
    """Where embedded sidecars are archived, and the tree their folder
    context is measured from. Defaults to the configured QUARANTINE_ROOT and
    MEDIA_ROOT; passed explicitly by callers (and tests) that must not touch
    the live quarantine. t_260921_182721_855: the tests' only other way in
    was a config redirect that silently lost to import order, so they
    archived fixtures into the production quarantine.
    """

    quarantine: Path
    media: Path

    @classmethod
    def configured(cls) -> "ArchiveRoots":
        """The production roots from organize_media_config."""
        return cls(quarantine=QUARANTINE_ROOT, media=MEDIA_ROOT)


def _archive_sidecars(video_path: Path, sidecars: list[Path],
                      roots: ArchiveRoots) -> None:
    """Move now-redundant sidecars to the existing quarantine mechanism,
    preserving name and parent-folder context (see
    media_temp_housekeeping.quarantine_orphan_subs for the sibling
    convention this mirrors)."""
    base = roots.quarantine / f"embedded-subtitles-{time.strftime('%Y%m%d')}"
    for sidecar in sidecars:
        if not sidecar.exists():
            continue
        try:
            rel_dir = sidecar.parent.relative_to(roots.media)
        except ValueError:
            rel_dir = Path(sidecar.parent.name)
        dest_dir = base / rel_dir
        try:
            dest_dir.mkdir(parents=True, exist_ok=True)
            dest = dest_dir / sidecar.name
            counter = 2
            while dest.exists():
                dest = dest_dir / f"{sidecar.stem}.{counter}{sidecar.suffix}"
                counter += 1
            # allow-bare-rename: archives an already-embedded subtitle sidecar
            # into quarantine after its content was burned into the video; the
            # video swap in this file already goes through replace_media_file
            # (verified separately by this test)
            shutil.move(str(sidecar), str(dest))
        except Exception as exc:
            log.warning("could not archive embedded sidecar %s: %s", sidecar, exc)


def _embed_verification_failure(before: dict, after: dict | None,
                                expected_langs: set[str],
                                expected_sub_count: int) -> str | None:
    """Return a bounded reason when post-embed invariants are not satisfied."""
    if not after:
        return "post-probe-unavailable"
    if not isinstance(before, dict) or not isinstance(after, dict):
        return "probe-shape-invalid"
    if after.get("video_codec") != before.get("video_codec"):
        return "video-codec-changed"
    if after.get("audio_codec") != before.get("audio_codec"):
        return "audio-codec-changed"
    if after.get("audio_channels") != before.get("audio_channels"):
        return "audio-channels-changed"
    after_subs = after.get("subtitle_streams") or []
    if not isinstance(after_subs, list):
        return "subtitle-streams-invalid"
    if len(after_subs) != expected_sub_count:
        return (f"subtitle-count-mismatch:expected={expected_sub_count}:"
                f"actual={len(after_subs)}")
    if any(not isinstance(stream, dict) for stream in after_subs):
        return "subtitle-stream-invalid"
    present_langs: set[str] = set()
    for stream in after_subs:
        language = stream.get("language")
        if language is not None and not isinstance(language, str):
            return "subtitle-stream-invalid"
        if stream.get("codec") in _TEXT_SUBTITLE_CODECS:
            present_langs.add(_canonical_lang(language))
    if not expected_langs <= present_langs:
        return ("subtitle-language-missing:expected="
                + ",".join(sorted(expected_langs))
                + ":present=" + ",".join(sorted(present_langs)))
    return None


def _verify_embed(before: dict, after: dict | None, expected_langs: set[str],
                  expected_sub_count: int) -> bool:
    """True only when the embed changed nothing but intended subtitles."""
    return _embed_verification_failure(
        before, after, expected_langs, expected_sub_count) is None


def _plan_embed(video_path: Path, ffmpeg: str,
                ) -> tuple[dict, list[dict], dict[str, tuple[Path, int | None]]] | None:
    """(before-probe, subtitle streams to keep, sidecars to (re-)embed), or
    None when there is nothing to do. Read-only."""
    sidecars = _finalized_srt_sidecars(video_path)
    if not sidecars:
        return None
    info = _probe_video_audio(video_path)
    if not info:
        return None
    existing_subs = info.get("subtitle_streams") or []
    existing_text_by_lang: dict[str, dict] = {}
    for s in existing_subs:
        lang = _canonical_lang(s.get("language"))
        if lang and s.get("codec") in _TEXT_SUBTITLE_CODECS and lang not in existing_text_by_lang:
            existing_text_by_lang[lang] = s

    to_embed: dict[str, tuple[Path, int | None]] = {}
    for raw_lang, sidecar in sidecars.items():
        lang = _canonical_lang(raw_lang)
        existing = existing_text_by_lang.get(lang)
        if existing is not None:
            if _content_matches(ffmpeg, video_path, existing["index"], sidecar):
                continue
            to_embed[lang] = (sidecar, existing["index"])
        else:
            to_embed[lang] = (sidecar, None)
    if not to_embed:
        return None
    replaced_indexes = {idx for _, idx in to_embed.values() if idx is not None}
    kept_subs = [s for s in existing_subs if s["index"] not in replaced_indexes]
    return info, kept_subs, to_embed


def _build_embed_cmd(ffmpeg: str, video_path: Path, tmp: Path, kept_subs: list[dict],
                     to_embed: dict[str, tuple[Path, int | None]], is_mp4: bool) -> list[str]:
    cmd = [ffmpeg, "-nostdin", "-y", "-i", str(video_path)]
    sidecar_inputs = list(to_embed.items())
    for _lang, (sidecar, _idx) in sidecar_inputs:
        cmd += ["-i", str(sidecar)]
    cmd += ["-map", "0:v", "-map", "0:a?"]
    for s in kept_subs:
        cmd += ["-map", f"0:{s['index']}"]
    for offset in range(len(sidecar_inputs)):
        cmd += ["-map", f"{offset + 1}:0"]
    if not is_mp4:
        cmd += ["-map", "0:t?"]
    cmd += ["-map", "0:d?", "-map_metadata", "0", "-map_chapters", "0",
            "-c:v", "copy", "-c:a", "copy"]
    for i in range(len(kept_subs)):
        cmd += [f"-c:s:{i}", "copy"]
    new_codec = "mov_text" if is_mp4 else "srt"
    for offset, (lang, (_sidecar, _idx)) in enumerate(sidecar_inputs):
        pos = len(kept_subs) + offset
        cmd += [f"-c:s:{pos}", new_codec,
                f"-metadata:s:s:{pos}", f"language={lang}",
                f"-disposition:s:{pos}", "0"]
    if is_mp4:
        cmd += ["-movflags", "+faststart"]
    cmd.append(str(tmp))
    return cmd


def _publish_embedded_candidate(
    video_path: Path,
    tmp: Path,
    to_embed: dict[str, tuple[Path, int | None]],
    before_replace: Callable[[], bool | None] | None,
    roots: ArchiveRoots,
) -> bool:
    """Publish one verified candidate only when playback is definitely idle."""
    tmp.chmod(stat.S_IMODE(video_path.stat().st_mode))
    if before_replace is not None:
        try:
            replace_allowed = before_replace() is True
        except Exception:  # Playback-state uncertainty must fail closed.
            replace_allowed = False
        if not replace_allowed:
            log.warning(
                "subtitle embed replacement refused by playback gate for %s; "
                "discarding output",
                video_path.name,
            )
            tmp.unlink(missing_ok=True)
            return False
    replace_media_file(tmp, video_path)
    _archive_sidecars(video_path, [sidecar for sidecar, _idx in to_embed.values()],
                      roots)
    log.info("embedded %d finalized subtitle(s) into %s: %s",
             len(to_embed), video_path.name, ", ".join(sorted(to_embed)))
    return True


def embed_finalized_subtitles(
    video_path: Path,
    *,
    before_replace: Callable[[], bool | None] | None = None,
    archive_roots: ArchiveRoots | None = None,
) -> bool:
    """Embed every finalized <stem>.<lang>.srt sidecar as a selectable text
    subtitle track (MKV: subrip, no re-encode; MP4/M4V: mov_text), replacing
    a stale same-language embedded track and skipping one that already
    matches (_plan_embed). Archives each embedded sidecar after ffprobe
    verifies the result. Never sets an embedded subtitle default (existing
    policy).

    Copy-only for video/audio; a no-op when there is nothing finalized to
    embed or every finalized sidecar already matches what is embedded. On any
    failure the original file and every sidecar are left untouched (the
    archive step only runs after verification succeeds).
    """
    if not video_path.exists():
        return False
    ffmpeg = require_tool("ffmpeg")
    planned = _plan_embed(video_path, ffmpeg)
    if planned is None:
        return False
    info, kept_subs, to_embed = planned
    is_mp4 = video_path.suffix.lower() in (".mp4", ".m4v")
    if is_mp4:
        for lang in list(to_embed):
            sidecar, _existing_index = to_embed[lang]
            spans = [
                (start / 1000.0, end / 1000.0)
                for start, end, _text in _parse_srt_cues(sidecar.read_bytes())
            ]
            overflow = text_gap_overflow(spans)
            if overflow is not None:
                # The mp4's mov_text track cannot carry a gap this wide: the
                # muxer would collapse the whole track onto the video's
                # opening (c298). The sidecar stays beside the video, where
                # Jellyfin plays it directly, instead of being embedded as a
                # zero-based track.
                log.info(
                    "not embedding %s into %s: its longest cue gap %.0fs "
                    "exceeds the %.0fs an mp4 mov_text track can represent "
                    "(the muxer would collapse the track to cues from "
                    "zero); leaving it as a sidecar",
                    sidecar.name, video_path.name, overflow,
                    MP4_TEXT_GAP_LIMIT_SECONDS,
                )
                del to_embed[lang]
        if not to_embed:
            return False

    try:
        with exclusive_media_write(video_path, "subembed") as tmp:
            cmd = _build_embed_cmd(ffmpeg, video_path, tmp, kept_subs, to_embed, is_mp4)

            try:
                result = run_encode(cmd, timeout=1800, progress_path=tmp)
            except Exception as exc:
                log.warning("subtitle embed failed for %s: %s", video_path.name, exc)
                tmp.unlink(missing_ok=True)
                return False
            if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size == 0:
                log.warning("subtitle embed failed for %s: rc=%s; ffmpeg said: %s",
                            video_path.name, result.returncode, _ffmpeg_stderr_tail(result))
                tmp.unlink(missing_ok=True)
                return False

            expected_sub_count = len(kept_subs) + len(to_embed)
            after_info = _probe_video_audio(tmp)
            verification_failure = _embed_verification_failure(
                info, after_info, set(to_embed), expected_sub_count)
            if verification_failure is not None:
                log.warning(
                    "subtitle embed verification failed for %s (%s); discarding output",
                    video_path.name,
                    verification_failure)
                tmp.unlink(missing_ok=True)
                return False

            return _publish_embedded_candidate(
                video_path, tmp, to_embed, before_replace,
                archive_roots or ArchiveRoots.configured())
    except MediaWriteBusy as exc:
        log.warning("subtitle embed skipped for %s: %s", video_path.name, exc)
        return False
