#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Post-move subtitle processing: extraction, provider fetches, and re-sync."""

from __future__ import annotations

import glob as globlib
import logging
import os
import re
import tempfile
from dataclasses import dataclass
from pathlib import Path

from lib.organize_media_config import (
    EXTRACT_ENGLISH_SRT,
    SYNC_SUBTITLES,
    SUBSYNC_MAX_OFFSET,
    SUBTITLE_TIMING_CACHE_DIR,
    PREFETCH_HASH_SUBS,
    PREFETCH_HASH_SUB_LANGS,
    PREFETCH_JIMAKU_SUBS,
    TV_ROOT,
)
from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.encode_priority import run_encode
from lib.media_probe import _ffprobe_subtitle_streams, _ffmpeg_stderr_tail
from lib import media_update_tracker
from lib.media_temp_housekeeping import SUBTITLE_TRANSIENT_MARKERS
from lib.subtitle_ad_filter import filter_srt_file
from lib.subtitle_language import function_word_ratio, looks_english, tokenize
from lib.subtitle_timing_alignment import Verdict, apply_transform, cue_starts_from_text, measure_alignment
from lib.subtitle_timing_reference import _TEXT_SUBTITLE_CODECS, _probe_text_subtitle_streams, reference_starts_for
from lib.media_tools import require_tool

log = logging.getLogger("organize-media")


@dataclass(frozen=True)
class MediaFileSnapshot:
    """Stat identity used to keep hash-timed subtitles tied to exact bytes."""

    device: int
    inode: int
    size: int
    mtime_ns: int
    ctime_ns: int

    @classmethod
    def capture(cls, path: Path) -> "MediaFileSnapshot | None":
        try:
            stat = path.stat()
        except OSError as exc:
            log.warning("could not snapshot media identity for %s: %s", path, exc)
            return None
        return cls(stat.st_dev, stat.st_ino, stat.st_size,
                   stat.st_mtime_ns, stat.st_ctime_ns)


@dataclass(frozen=True)
class EpisodeContext:
    """Resolved TV identity used for an exact catalogue lookup."""

    show: str
    season: int
    episode: int


@dataclass(frozen=True)
class SubtitleAcquisition:
    """Result of one source-byte subtitle acquisition attempt.

    ``hash_sidecar_suffixes`` stores names relative to the source stem, so
    callers can map them after a move or extension/name change. The two
    snapshots make a hash match eligible for sync bypass only when neither
    acquisition nor later media processing changed the source identity.
    """

    source_before: MediaFileSnapshot | None
    source_after: MediaFileSnapshot | None
    hash_sidecar_suffixes: tuple[str, ...]
    episode_context: EpisodeContext | None

    def hash_paths_for_unchanged_media(self, video_path: Path) -> set[Path]:
        """Map hash-timed sidecars onto ``video_path`` only for unchanged bytes."""
        current = MediaFileSnapshot.capture(video_path)
        if (self.source_before is None or self.source_before != self.source_after
                or current != self.source_after):
            return set()
        return {video_path.parent / f"{video_path.stem}{suffix}"
                for suffix in self.hash_sidecar_suffixes}


def _sidecar_suffixes(video_path: Path, sidecars: list[Path]) -> tuple[str, ...]:
    prefix = video_path.stem + "."
    return tuple(sorted({path.name[len(video_path.stem):] for path in sidecars
                         if path.name.startswith(prefix)}))


def _has_english_sidecar(dest_dir: Path, base_name: str) -> bool:
    for ext in (".srt", ".ass", ".ssa", ".vtt"):
        for code in ("en", "eng"):
            if (dest_dir / f"{base_name}.{code}{ext}").exists():
                return True
    return False


def _embedded_subtitle_languages_from(streams: list[dict]) -> set[str]:
    """Lowercased language codes already embedded, from an already-probed
    subtitle stream list (see _embedded_subtitle_languages)."""
    langs = set()
    for stream in streams:
        lang = ((stream.get("tags") or {}).get("language") or "").strip().lower()
        if lang:
            langs.add(lang)
    return langs


def _embedded_subtitle_languages(video_file: Path) -> set[str]:
    """Lowercased language codes already embedded as subtitle streams.

    Used to make embedded-subtitle detection count as "already have this
    language" for the extract/fetch stages below (design section 4.2) -- the
    same way an existing sidecar already does -- so a subtitle this pipeline
    (or the source file itself) already embedded is never re-extracted,
    re-fetched, or re-synced from scratch.
    """
    return _embedded_subtitle_languages_from(_ffprobe_subtitle_streams(video_file))


def _extract_stream_to_srt(video_file: Path, index: int, out_path: Path) -> bool:
    """Demux subtitle stream `index` of `video_file` to `out_path` via the
    supervised ffmpeg wrapper. Shared by both extraction paths in
    _extract_embedded_english_srt below (tagged eng/en, and untagged
    candidate) -- one call shape, one failure-cleanup rule.

    :returns: True on success (`out_path` exists and is non-empty). False on
        any ffmpeg/IO failure, with any partial `out_path` removed first.
    """
    ffmpeg = require_tool("ffmpeg")
    try:
        # Demuxes the whole container to pull one stream out, and used to run
        # with no clamp AND no timeout at all: a hung ffmpeg here would have
        # stalled the pipeline indefinitely. progress_path is the sidecar being
        # written, so the stall watchdog can tell "slow" from "stuck".
        result = run_encode(
            [ffmpeg, "-y", "-i", str(video_file), "-map", f"0:{index}", str(out_path)],
            timeout=1800, progress_path=out_path,
        )
        if result.returncode == 0 and out_path.exists() and out_path.stat().st_size > 0:
            return True
    except Exception:
        # allow-silent-catch: best-effort extraction; on any ffmpeg/IO failure
        # no sidecar is left behind (caller has no dependency on this succeeding).
        pass
    if out_path.exists():
        out_path.unlink(missing_ok=True)
    return False


def _extract_embedded_english_srt(video_file: Path):
    if not EXTRACT_ENGLISH_SRT:
        return
    require_tool("ffmpeg")
    base_name = video_file.stem
    dest_dir = video_file.parent
    if _has_english_sidecar(dest_dir, base_name):
        return

    streams = _ffprobe_subtitle_streams(video_file)
    # An English track already embedded counts the same way an existing
    # sidecar does: nothing missing, nothing to do. Without this, a second
    # pipeline pass over an already-finalized file (its sidecar archived by
    # embed_finalized_subtitles after a prior run) would re-extract the very
    # track it just embedded, straight back into a fresh sidecar -- reopening
    # the extract-to-embed-to-extract cycle the archive decision exists to
    # close (design section 4.2).
    if {"eng", "en"} & _embedded_subtitle_languages_from(streams):
        return

    out_path = dest_dir / f"{base_name}.en.srt"
    if out_path.exists():
        return

    target_stream = None
    for stream in streams:
        tags = stream.get("tags") or {}
        lang = (tags.get("language") or "").lower()
        if lang in {"eng", "en"}:
            target_stream = stream
            break

    if target_stream is not None:
        index = target_stream.get("index")
        if index is None:
            return
        if _extract_stream_to_srt(video_file, index, out_path):
            filter_srt_file(out_path, dry_run=False)
            media_update_tracker.record(out_path)
        return

    # No stream carries an eng/en language tag at all. Some rips (the Pitt
    # S01 720p AMZN "MIKE" release, diagnosed 2026-09-23) ship their only
    # English subtitle track completely untagged -- ffprobe reports "" or
    # "und", not "eng"/"en" -- so the lookup above never finds it. Consider
    # every untagged (missing or "und") text-subtitle stream in stream
    # order and accept the first one whose extracted text actually reads as
    # English (subtitle_language.looks_english): a French or Japanese track
    # that also happens to ship untagged must not be mistaken for English
    # just because nothing else claimed the "eng" tag.
    for stream in _probe_text_subtitle_streams(video_file):
        codec = (stream.get("codec_name") or "").lower()
        if codec not in _TEXT_SUBTITLE_CODECS:
            continue
        lang = (stream.get("tags") or {}).get("language", "")
        if (lang or "").strip().lower() not in ("", "und"):
            continue
        index = stream.get("index")
        if index is None:
            continue
        candidate_path = dest_dir / f"{base_name}.s{index}.candidate.srt"
        if not _extract_stream_to_srt(video_file, index, candidate_path):
            continue
        text = candidate_path.read_text(encoding="utf-8", errors="ignore")
        if looks_english(text):
            # allow-bare-rename: candidate_path
            # (f"{base_name}.s{index}.candidate.srt") and out_path
            # (f"{base_name}.en.srt") are both extracted *.srt subtitle
            # sidecars built by this function; never a video file.
            candidate_path.replace(out_path)
            filter_srt_file(out_path, dry_run=False)
            media_update_tracker.record(out_path)
            return
        ratio = function_word_ratio(tokenize(text))
        log.info(
            "untagged subtitle stream %d in %s did not read as English "
            "(function-word ratio %.3f); rejected",
            index, video_file.name, ratio,
        )
        candidate_path.unlink(missing_ok=True)


# Sentinel distinguishing "caller did not pass a reference, look it up"
# from "caller looked it up and it is genuinely None (no embedded English
# track)" -- reference_starts_for()'s own return type already uses None for
# the latter, so a plain default of None cannot tell the two apart.
_REFERENCE_UNSET = object()


def _new_sync_temp(sub_path: Path) -> Path:
    """Create a unique hidden sync output beside its subtitle source."""
    descriptor, name = tempfile.mkstemp(
        prefix=f".{sub_path.stem}.syncing.", suffix=".srt",
        dir=sub_path.parent)
    os.close(descriptor)
    Path(name).unlink()
    return Path(name)


def _sync_subtitle_deterministically(sub_path: Path, reference: tuple[list[float], str]) -> bool:
    """Timing-gate path: measure `sub_path` against `reference`'s cue starts
    and, when they diverge by a constant offset or frame-rate scale, rewrite
    the sidecar under that transform. Never runs ffsubsync -- when a video's
    own embedded English track is available, the deterministic comparison
    replaces the audio-VAD heuristic for that sidecar entirely.

    Returns True if a corrected file was written. Returns False when the
    sidecar was already in sync (nothing to write), had no usable timing
    correspondence at all (quarantined by rename, not deleted), had too few
    cues to measure confidently (left untouched), or on any I/O error.
    """
    reference_starts, reference_name = reference
    try:
        original_text = sub_path.read_text(encoding="utf-8", errors="ignore")
    except OSError as exc:
        log.warning("timing gate: could not read %s: %s", sub_path.name, exc)
        return False

    result = measure_alignment(cue_starts_from_text(original_text), reference_starts)
    if result.verdict == Verdict.IN_SYNC:
        # Timing is correct, but the file may still carry whole-cue adverts:
        # filter them now so a resync pass gradually cleans existing sidecars.
        from lib.subtitle_ad_filter import filter_srt_text as _filter_text

        filtered, removed, _ = _filter_text(original_text)
        if removed == 0:
            return False
        tmp = _new_sync_temp(sub_path)
        try:
            tmp.write_text(filtered, encoding="utf-8")
            # allow-bare-rename: installs a filtered *.srt subtitle sidecar;
            # advert removal only, never video bytes.
            tmp.replace(sub_path)
        except OSError as exc:
            log.warning("timing gate: could not write filtered %s: %s", sub_path.name, exc)
            tmp.unlink(missing_ok=True)
            return False
        log.info("timing gate: filtered %d advert cue(s) from %s (already in sync)",
                 removed, sub_path.name)
        return True
    if result.verdict == Verdict.INSUFFICIENT:
        return False
    if result.verdict == Verdict.UNRELATED:
        unsynced = sub_path.with_name(sub_path.stem + ".unsynced.srt")
        try:
            # allow-bare-rename: quarantines an unrelated *.srt subtitle
            # sidecar; this function's only caller (_sync_subtitle_to_video)
            # already refuses to reach here unless
            # sub_path.suffix.lower() == ".srt", so sub_path is never a
            # video file.
            sub_path.replace(unsynced)
        except OSError as exc:
            log.warning("timing gate: could not quarantine unrelated %s: %s", sub_path.name, exc)
            return False
        log.warning("timing gate: %s has no timing correspondence with %s; quarantined as %s",
                    sub_path.name, reference_name, unsynced.name)
        return False

    # SHIFTED: apply the recovered transform and require the result to
    # re-measure as IN_SYNC before installing it -- a transform that does not
    # actually land in sync is worse than doing nothing.
    corrected = apply_transform(original_text, result.best_scale, result.best_offset)
    recheck = measure_alignment(cue_starts_from_text(corrected), reference_starts)
    if recheck.verdict != Verdict.IN_SYNC:
        log.warning(
            "timing gate: correction for %s did not reach IN_SYNC "
            "(scale=%s offset=%s -> verdict=%s match_frac=%.3f); leaving original untouched",
            sub_path.name, result.best_scale, result.best_offset,
            recheck.verdict.value, recheck.best_match_frac,
        )
        return False
    from lib.subtitle_ad_filter import filter_srt_text as _filter_shifted

    corrected, _, _ = _filter_shifted(corrected)
    tmp = _new_sync_temp(sub_path)
    try:
        tmp.write_text(corrected, encoding="utf-8")
        # allow-bare-rename: installs a resynced *.srt subtitle sidecar; this
        # transform only ever operates on subtitle text, never video bytes.
        tmp.replace(sub_path)
    except OSError as exc:
        log.warning("timing gate: could not write corrected %s: %s", sub_path.name, exc)
        tmp.unlink(missing_ok=True)
        return False
    log.info("timing gate: retimed %s against %s (scale=%.6f offset=%.3fs)",
             sub_path.name, reference_name, result.best_scale, result.best_offset)
    return True


def _sync_subtitle_to_video(video_path: Path, sub_path: Path, *,
                             reference: tuple[list[float], str] | None | object = _REFERENCE_UNSET,
                             allow_audio_fallback: bool = True) -> bool:
    """Re-align an SRT sidecar to a video.

    When `video_path` has its own embedded English text subtitle track
    (`reference_starts_for`), this uses the deterministic cue-start timing
    gate instead of ffsubsync: see _sync_subtitle_deterministically(). Only
    when there is no such reference does this fall back to the original
    ffsubsync (audio VAD) path below.

    Replaces the sidecar in place on success.  Returns True if a synced file
    was written.  Returns False (and leaves the original untouched) on any
    error -- ffsubsync will refuse rather than corrupt timing if it can't get
    a confident alignment.

    :param reference: Pre-looked-up (cue_starts, reference_name) tuple, or
        None if the caller already determined there is no reference. Left
        unset, this looks the reference up itself (the shape a direct call
        site like tools/sync-recent-subs.py uses).
    :param allow_audio_fallback: When there is no reference, whether the
        ffsubsync path may run at all. False for sidecars already trusted by
        an exact hash match (`_sync_sidecar_subtitles`'s `skip` set) -- those
        are still measured against a reference when one exists, but never
        handed to the audio heuristic.
    """
    if not SYNC_SUBTITLES:
        return False
    if sub_path.suffix.lower() != ".srt" or not sub_path.exists():
        return False

    if reference is _REFERENCE_UNSET:
        reference = reference_starts_for(video_path, SUBTITLE_TIMING_CACHE_DIR)
    if reference is not None:
        return _sync_subtitle_deterministically(sub_path, reference)
    if not allow_audio_fallback:
        return False

    # Overridable so tests can point at a boundary double (ffsubsync isn't on
    # PATH, so shutil.which() can't be used to inject one the way the other
    # pipeline stages' ffmpeg lookups can).
    ffsubsync_override = os.environ.get("SYNC_FFSUBSYNC_BIN")
    ffsubsync = (Path(ffsubsync_override) if ffsubsync_override else
                 Path(__file__).resolve().parent.parent / "ffsubsync-venv" / "bin" / "ffsubsync")
    if not ffsubsync.exists():
        return False
    tmp = _new_sync_temp(sub_path)
    try:
        result = run_cancellable_capture(
            [str(ffsubsync), str(video_path),
             "-i", str(sub_path),
             "-o", str(tmp),
             "--max-offset-seconds", str(SUBSYNC_MAX_OFFSET)],
            timeout=1800,
        )
        if result.returncode != 0 or not tmp.exists() or tmp.stat().st_size == 0:
            log.warning("ffsubsync failed for %s: rc=%s; ffsubsync said: %s",
                        sub_path.name, result.returncode, _ffmpeg_stderr_tail(result))
            tmp.unlink(missing_ok=True)
            return False
        # ffsubsync prints the chosen offset to stderr; surface it for visibility.
        for line in (result.stderr or "").splitlines():
            if "offset" in line.lower() and "second" in line.lower():
                log.info("subsync %s: %s", sub_path.name, line.strip())
                break
        from lib.subtitle_ad_filter import filter_srt_text as _filter_ffsubsync

        try:
            tmp_text = tmp.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError) as exc:
            log.warning("subsync %s: could not read ffsubsync output for filtering: %s",
                        sub_path.name, exc)
            tmp.unlink(missing_ok=True)
            return False
        filtered_tmp, _, _ = _filter_ffsubsync(tmp_text)
        try:
            tmp.write_text(filtered_tmp, encoding="utf-8")
        except OSError as exc:
            log.warning("subsync %s: could not write filtered output: %s", sub_path.name, exc)
            tmp.unlink(missing_ok=True)
            return False
        # allow-bare-rename: _sync_subtitle_to_video installs a resynced *.srt
        # subtitle sidecar; ffsubsync only ever operates on subtitle text,
        # never video bytes
        tmp.replace(sub_path)
        return True
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("ffsubsync failed for %s: %s", sub_path.name, exc)
        tmp.unlink(missing_ok=True)
        return False


def _sync_sidecar_subtitles(video_path: Path, skip: set[Path] | None = None):
    """Run the timing gate (or ffsubsync, with no reference available)
    against every .srt sidecar sharing the video's stem.

    `skip` is a set of paths already hash-matched (guaranteed in-sync by an
    exact provider hash lookup, not audio/timing analysis). These are still
    measured against the timing gate when the video has its own embedded
    English reference -- a hash match this pipeline trusts should also
    measure as IN_SYNC, and if it does not, that is itself worth catching --
    but they are never handed to the audio-based ffsubsync fallback.
    """
    if not SYNC_SUBTITLES or not video_path.exists():
        return
    skip = skip or set()
    base = video_path.stem
    subs = [sub for sub in sorted(video_path.parent.glob(globlib.escape(base) + "*.srt"))
            # Skip transient files left by interrupted runs.
            if not any(marker in sub.name for marker in SUBTITLE_TRANSIENT_MARKERS)]
    if not subs:
        return
    reference = reference_starts_for(video_path, SUBTITLE_TIMING_CACHE_DIR)
    for sub in subs:
        _sync_subtitle_to_video(video_path, sub, reference=reference,
                                allow_audio_fallback=sub not in skip)


def _prefetch_hash_subtitles(
        video_path: Path, *, episode_context: EpisodeContext | None = None
        ) -> list[Path]:
    """Fetch any hash-matched subtitles available for this exact encode.

    Calls subliminal-hash-fetch.py via the subliminal venv.  Returns the list
    of newly-written sidecar paths so the caller can skip ffsubsync on them
    (hash matches are timed correctly already).
    """
    if not PREFETCH_HASH_SUBS or not video_path.exists():
        return []
    tools_dir = Path(__file__).resolve().parent.parent
    script = tools_dir / "subliminal-hash-fetch.py"
    venv_py = tools_dir / "subliminal-venv" / "bin" / "python3"
    if not script.exists() or not venv_py.exists():
        return []
    # Cheap English prefilter. The subprocess performs the complete canonical
    # language comparison for every requested language before provider lookup.
    embedded = _embedded_subtitle_languages(video_path)
    requested = [c.strip() for c in PREFETCH_HASH_SUB_LANGS.split(",") if c.strip()]
    languages = [c for c in requested if not (c.lower() == "en" and {"en", "eng"} & embedded)]
    if not languages:
        return []
    base = video_path.stem
    before = {p for p in video_path.parent.glob(globlib.escape(base) + "*.srt")}
    helper_failed = False
    context = episode_context or _library_episode_context(video_path)
    command = [str(venv_py), str(script), "--video", str(video_path),
               "--languages", ",".join(languages)]
    if context is not None:
        command.extend(["--show", context.show, "--season", str(context.season),
                        "--episode", str(context.episode)])
    try:
        result = run_cancellable_capture(
            command,
            timeout=300,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout).strip().splitlines()
            log.warning("hash-fetch failed for %s (exit %d): %s",
                        video_path.name, result.returncode,
                        detail[-1] if detail else "no diagnostic output")
            for line in detail[:-1]:
                log.warning("hash-fetch: %s", line.strip())
            helper_failed = True
        if result.stderr:
            for line in result.stderr.splitlines():
                if ("hash-matched" in line or "saved" in line
                        or "source lookup identity:" in line):
                    log.info("hash-fetch: %s", line.strip())
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("hash-fetch failed for %s: %s", video_path.name, exc)
        return []
    after = {p for p in video_path.parent.glob(globlib.escape(base) + "*.srt")}
    new = sorted(after - before)
    for p in new:
        if p.suffix.lower() == ".srt":
            filter_srt_file(p, dry_run=False)
        media_update_tracker.record(p)
    if not new and not helper_failed:
        log.info("hash-fetch: no new exact-hash subtitle for %s (languages=%s)",
                 video_path.name, ",".join(languages))
    return [] if helper_failed else new


def _library_episode_context(video_path: Path) -> EpisodeContext | None:
    """Resolve a TV show's folder identity and a single filename episode."""
    try:
        relative = video_path.resolve().relative_to(TV_ROOT.resolve())
    except (OSError, ValueError):
        return None
    if len(relative.parts) < 3:
        return None
    season_match = re.fullmatch(r"Season\s+(\d+)", relative.parts[-2], re.IGNORECASE)
    if season_match is None:
        return None
    from guessit import guessit
    from lib.show_identity import _show_name_from_folder

    episode_info = guessit(video_path.name, {"type": "episode"})
    episode = episode_info.get("episode")
    if isinstance(episode, list):
        episode = episode[0] if len(episode) == 1 else None
    if not isinstance(episode, int):
        return None
    show_folder = TV_ROOT / relative.parts[0]
    show = _show_name_from_folder(show_folder / "episode.mkv")
    if not show:
        return None
    return EpisodeContext(show, int(season_match.group(1)), episode)


def _prefetch_jimaku_subtitle(
        video_path: Path, *, episode_context: EpisodeContext | None = None
        ) -> list[Path]:
    """Fetch an episode-matched Japanese subtitle when no JA sidecar exists."""
    if not PREFETCH_JIMAKU_SUBS or not video_path.exists():
        return []
    if episode_context is None:
        resolved = _library_episode_context(video_path)
        if resolved is None:
            return []
        episode_context = resolved
    for code in ("ja", "jpn"):
        for ext in (".srt", ".ass", ".ssa", ".vtt"):
            if video_path.with_suffix(f".{code}{ext}").exists():
                return []
    if {"ja", "jpn"} & _embedded_subtitle_languages(video_path):
        return []
    tools_dir = Path(__file__).resolve().parent.parent
    script = tools_dir / "jimaku-fetch.py"
    venv_py = tools_dir / "subliminal-venv" / "bin" / "python3"
    if not script.exists() or not venv_py.exists():
        return []
    before = set(video_path.parent.glob(globlib.escape(video_path.stem) + ".ja*"))
    try:
        result = run_cancellable_capture(
            [str(venv_py), str(script), "--video", str(video_path),
             "--show", episode_context.show,
             "--season", str(episode_context.season),
             "--episode", str(episode_context.episode)],
            timeout=180,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout).strip().splitlines()
            log.warning("jimaku-fetch failed for %s (show=%s season=%s episode=%s, exit %d): %s",
                        video_path.name, episode_context.show,
                        episode_context.season, episode_context.episode,
                        result.returncode,
                        detail[-1] if detail else "no diagnostic output")
            return []
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("jimaku-fetch failed for %s: %s", video_path.name, exc)
        return []
    after = set(video_path.parent.glob(globlib.escape(video_path.stem) + ".ja*"))
    new = sorted(after - before)
    if not new and "No matching Japanese subtitle found" in result.stdout:
        log.info("jimaku-fetch: no catalogue match for %s S%02dE%02d (%s)",
                 video_path.name, episode_context.season,
                 episode_context.episode, episode_context.show)
    for path in new:
        if path.suffix.lower() == ".srt":
            filter_srt_file(path, dry_run=False)
        media_update_tracker.record(path)
        log.info("jimaku-fetch: saved %s", path.name)
    return new


def acquire_subtitle_sidecars(
        video_path: Path, *,
        episode_context: EpisodeContext | None = None
        ) -> SubtitleAcquisition:
    """Attempt hash and episode-catalogue fetches against the current bytes."""
    resolved_context = episode_context or _library_episode_context(video_path)
    before = MediaFileSnapshot.capture(video_path)
    fetched = _prefetch_hash_subtitles(video_path, episode_context=resolved_context)
    _prefetch_jimaku_subtitle(
        video_path, episode_context=resolved_context)
    after = MediaFileSnapshot.capture(video_path)
    return SubtitleAcquisition(
        source_before=before,
        source_after=after,
        hash_sidecar_suffixes=_sidecar_suffixes(video_path, fetched),
        episode_context=resolved_context,
    )
