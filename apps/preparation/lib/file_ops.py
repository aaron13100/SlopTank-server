#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Move a video (and its sidecars) into its destination folder.

Owns the physical file-move mechanics: name sanitizing, collision handling via
quarantine/versioning, and labeling a subtitle sidecar left without a language
code once it lands next to its video.
"""

from __future__ import annotations

import glob as globlib
import logging
import re
from pathlib import Path

from lib.organize_media_config import QUARANTINE_ROOT, SUBTITLE_EXTS, VIDEO_EXTS
from lib import media_identity
from lib import media_update_tracker

log = logging.getLogger("organize-media")

# Extensions the organizer carries alongside a video it moves. The subtitle
# half comes from the shared SUBTITLE_EXTS so it cannot drift from what the
# server indexes; an extension the pipeline PRODUCES but this set omits is a
# subtitle that silently stays behind in the source directory, which is what
# happened to the ".sup" sidecars the transcode pass extracts.
_ARTWORK_SIDECAR_EXTS = {".nfo", ".jpg", ".jpeg", ".png", ".webp"}
_SIDECAR_EXTS = SUBTITLE_EXTS | _ARTWORK_SIDECAR_EXTS
# Subtitle sidecars whose language can be sniffed from their text content.
_TEXT_SUBTITLE_SIDECAR_EXTS = {".srt", ".ass", ".ssa"}

_KNOWN_SUB_LANGS = {
    "en",
    "eng",
    "ja",
    "jpn",
    "fr",
    "fra",
    "fre",
    "es",
    "spa",
    "de",
    "deu",
    "ger",
    "ar",
    "hr",
    "tr",
    "tur",
}


_LANG_WORDS = {
    "en": ["the", "and", "you", "your", "with", "that", "this", "for", "are", "not"],
    "fr": ["le", "la", "les", "de", "des", "et", "est", "pas", "une", "vous"],
    "es": ["el", "la", "los", "las", "de", "que", "y", "no", "una", "usted"],
    "de": ["der", "die", "das", "und", "nicht", "ich", "du", "ist", "ein", "eine"],
    "hr": ["da", "ne", "je", "sam", "si", "smo", "ste", "su", "sto", "što", "ovo", "nije"],
}


_FILENAME_LANG_HINTS = {
    "english": "en",
    "eng": "en",
    "french": "fr",
    "francais": "fr",
    "français": "fr",
    "spanish": "es",
    "espanol": "es",
    "español": "es",
    "german": "de",
    "deutsch": "de",
    "japanese": "ja",
    "nihongo": "ja",
    "turkish": "tr",
    "turkce": "tr",
}


def safe_name(name: str) -> str:
    name = re.sub(r"[\\/:*?\"<>|]", " ", name)
    name = re.sub(r"\s+", " ", name).strip()
    return name


def _fix_film_title(info: dict, file_path: Path, title: str) -> str:
    """Recover title prefix stripped by guessit's 'film' parser.

    guessit parses 'F1 The Movie 2025...' as film=1, title='The Movie',
    losing the 'F1' prefix.  When a film number is present, look for the
    title string in the stem (up to the year) and treat whatever comes
    before it as the missing prefix.
    """
    film = info.get("film")
    if film is None or not isinstance(film, int) or not title:
        return title
    stem_no_year = re.sub(r"[\. ]\d{4}[\. ].*", "", file_path.stem, flags=re.IGNORECASE).strip()
    idx = stem_no_year.lower().find(title.lower())
    if 0 < idx <= 6:
        prefix = stem_no_year[:idx].strip()
        if prefix:
            return f"{prefix} {title}"
    return title


def unique_path(dest: Path) -> Path:
    if not dest.exists():
        return dest
    for i in range(1, 1000):
        candidate = dest.with_name(f"{dest.stem}__dup{i}{dest.suffix}")
        if not candidate.exists():
            return candidate
    raise RuntimeError(f"Could not find unique path for {dest}")


def _already_in_quarantine(src: Path) -> bool:
    """Return True if an identical copy of src already exists in quarantine."""
    try:
        src_stat = src.stat()
    except Exception:
        return False
    for candidate in QUARANTINE_ROOT.glob(
            f"{globlib.escape(src.stem)}*{globlib.escape(src.suffix)}"):
        try:
            cs = candidate.stat()
            if cs.st_size == src_stat.st_size and cs.st_mtime == src_stat.st_mtime:
                return True
        except Exception:
            continue
    return False


def move_to_quarantine(src: Path):
    QUARANTINE_ROOT.mkdir(parents=True, exist_ok=True)
    # If an identical copy is already in quarantine (e.g. from a previous run
    # where the copy succeeded but the source delete failed), just remove the
    # source here without re-copying.
    if _already_in_quarantine(src):
        log.info("quarantine: %s already quarantined, removing source", src.name)
        try:
            src.unlink()
        except Exception as exc:
            log.warning("quarantine: could not remove already-quarantined source %s: %s", src.name, exc)
        return
    dest = QUARANTINE_ROOT / src.name
    dest = unique_path(dest)
    try:
        media_identity.move_media_file(src, dest)
    except Exception as exc:
        # move_media_file's cross-device branch copies then unlinks; if the
        # copy succeeded but unlink failed (e.g. permission issue on the
        # source), the file is now in quarantine but the original is still
        # in downloads.  Log and carry on -- next run will detect the
        # duplicate via _already_in_quarantine and attempt a clean unlink.
        if dest.exists():
            log.warning("quarantine: copied %s but could not remove source: %s", src.name, exc)
        else:
            log.error("quarantine: failed to move %s: %s", src.name, exc)
            raise


def _has_same_stem_video(destination: Path) -> bool:
    """Find an existing video edition regardless of its container or case."""
    return any(
        candidate.stem.casefold() == destination.stem.casefold()
        and candidate.suffix.lower() in VIDEO_EXTS
        and candidate.is_file()
        for candidate in destination.parent.iterdir()
    )


def move_with_sidecars(src: Path, dest_dir: Path, dest_name: str = None, allow_versioning: bool = False, version_label: str = ""):
    dest_dir.mkdir(parents=True, exist_ok=True)
    if dest_name is None:
        dest_name = src.stem
    if allow_versioning and version_label:
        dest_name = f"{dest_name} - {version_label}"
    dest_file = dest_dir / (dest_name + src.suffix)
    # An unversioned reimport must not return under another container suffix:
    # conversion would otherwise publish it beside the existing edition.
    if dest_file.exists() or (not allow_versioning and _has_same_stem_video(dest_file)):
        if allow_versioning:
            dest_file = unique_path(dest_file)
            dest_name = dest_file.stem
        else:
            log.info("quarantine (dest exists): %s", src.name)
            move_to_quarantine(src)
            return None, None
    log.info("move %s -> %s", src.name, dest_file)
    media_identity.move_media_file(src, dest_file)
    media_update_tracker.record(dest_file)

    # move sidecars that share the original stem
    moved_subs = []
    for sidecar in src.parent.glob(globlib.escape(src.stem) + ".*"):
        if sidecar == src:
            continue
        if sidecar.suffix.lower() not in _SIDECAR_EXTS:
            continue
        # Everything between the stem and the extension is the sidecar's
        # identity -- for a subtitle, the language and the forced/default
        # flags Emby.Naming reads back off the filename. Keeping only the
        # extension both anonymizes the track and makes every language of one
        # format collide on a single destination name, so "Show.eng.srt" and
        # "Show.fre.srt" arrived as one file and a quarantined leftover.
        side_dest = dest_dir / (dest_name + sidecar.name[len(src.stem):])
        if side_dest.exists():
            move_to_quarantine(sidecar)
        else:
            media_identity.move_media_file(sidecar, side_dest)
            if side_dest.suffix.lower() in _TEXT_SUBTITLE_SIDECAR_EXTS:
                moved_subs.append(side_dest)
    _normalize_undefined_subtitles(dest_dir, dest_name, dest_file, moved_subs)
    return dest_file, dest_name


def _detect_sub_language_from_filename(name: str) -> str:
    low = name.lower()
    for key, code in _FILENAME_LANG_HINTS.items():
        if re.search(rf"\b{re.escape(key)}\b", low):
            return code
    return ""


def _detect_sub_language(sub_path: Path) -> str:
    try:
        data = sub_path.read_bytes()[:40000]
    except Exception:
        return ""
    if not data:
        return ""
    text = data.decode("utf-8", errors="ignore")
    if not text:
        return ""
    # Japanese detection via Unicode ranges (Hiragana/Katakana/CJK).
    for ch in text:
        o = ord(ch)
        if 0x3040 <= o <= 0x30FF or 0x4E00 <= o <= 0x9FFF:
            return "ja"
        if 0x0600 <= o <= 0x06FF or 0x0750 <= o <= 0x077F or 0x08A0 <= o <= 0x08FF:
            return "ar"
    low = text.lower()
    scores = {k: 0 for k in _LANG_WORDS}
    for lang, words in _LANG_WORDS.items():
        for w in words:
            scores[lang] += len(re.findall(rf"\b{re.escape(w)}\b", low))
    # Small boosts for distinctive punctuation
    if "¿" in text or "¡" in text:
        scores["es"] += 2
    if "œ" in low or "ç" in low:
        scores["fr"] += 2
    # Pick best if sufficiently strong.
    best_lang, best_score = max(scores.items(), key=lambda kv: kv[1])
    if best_score < 3:
        return ""
    sorted_scores = sorted(scores.values(), reverse=True)
    if len(sorted_scores) > 1 and best_score < sorted_scores[1] + 2:
        return ""
    return best_lang


def _extract_lang_token(base_name: str, subtitle_path: Path) -> str:
    stem = subtitle_path.stem
    if not stem.startswith(base_name + "."):
        return ""
    suffix = stem[len(base_name) + 1 :]
    parts = [p.lower() for p in suffix.split(".") if p]
    for part in parts:
        if part in _KNOWN_SUB_LANGS:
            return part
    return ""


def _normalize_undefined_subtitles(dest_dir: Path, base_name: str, dest_file: Path, moved_subs: list[Path]):
    if not moved_subs:
        return
    # If we already have a labeled subtitle, do nothing.
    for sub in dest_dir.glob(globlib.escape(base_name) + ".*.srt"):
        if _extract_lang_token(base_name, sub):
            return
    # Only rename when we have a single unlabeled subtitle and audio is English.
    unlabeled = [sub for sub in moved_subs if sub.suffix.lower() == ".srt" and not _extract_lang_token(base_name, sub)]
    if len(unlabeled) != 1:
        return
    detected = _detect_sub_language_from_filename(unlabeled[0].stem) or _detect_sub_language(unlabeled[0])
    if not detected:
        return
    target = dest_dir / f"{base_name}.{detected}.srt"
    if target.exists():
        return
    # allow-bare-rename: _normalize_undefined_subtitles renames a
    # freshly-moved-in *.srt subtitle sidecar in place to append its detected
    # language tag. The video and every sidecar move are already complete.
    unlabeled[0].rename(target)
    media_update_tracker.record(target)
