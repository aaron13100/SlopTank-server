#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Conservative whole-cue advert filter for SRT sidecar subtitles (c297).

Strips pirate-site streaming adverts, OpenSubtitles site adverts, and
release-group / subtitle-site whole-cue promos observed library-wide, while
preserving dialogue that mentions a website and translator/sync credits.

Matching is whole-cue only: a cue is dropped only when its entire text (after
stripping markup, collapsing whitespace, and lowercasing for comparison)
matches an advert template. Substring matches never delete.

Scope (enumerated 2026-09-26 via Jellyfin item Paths over 467 .srt files,
384169 cues; see deliverables/t_260925_230105_016/ad-variants.jsonl):
  - Pirate streaming: "[ Y2flix.cc - Watch Movies and TV Shows Online for
    Free ]" (23 files) plus a generic domain + "watch ... online for free"
    template for future variants.
  - OpenSubtitles: task-listed "Advertise your product or brand here",
    "Support us and become VIP member", "Please rate this subtitle at"
    (0 observed, handled for future downloads) plus observed "Subtitles
    downloaded from www.OpenSubtitles.org" and "Download Movie Subtitles
    Searcher from www.OpenSubtitles.org".
  - Release-group / subtitle-site promos observed: YIFY-TORRENTS (2
    variants), PSArips, AllSubs, Subscene, titlovi (2 variants).
  - Preserved (out of scope): translator/sync credits naming an individual
    or team (addic7ed, argenteam, 8 variants) and all dialogue mentioning a
    website (5 negative-control variants).

File handling: UTF-8-SIG read (BOM tolerated, removed on write), malformed
or undecodable files left byte-identical, clean files never rewritten (so
mtime-gated passes like sync-recent-subs do not resync them), and advert
removal published atomically via a same-directory temp plus os.replace.
"""

from __future__ import annotations

import os
import re
import tempfile
from pathlib import Path

_TIMESTAMP_LINE_RE = re.compile(
    r"^\s*\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,.]\d{3}.*$"
)
_OVERRIDE_TAG_RE = re.compile(r"\{\\[^}]*\}")
_MARKUP_TAG_RE = re.compile(r"<[^>]*>")
_WS_RE = re.compile(r"\s+")
_WATCH_FREE_AD_RE = re.compile(
    r"^(?:www\.)?[a-z0-9-]+\."
    r"(?:cc|to|com|net|org|io|me|tv|site|online|fun|icu|xyz|info|biz)"
    r"\s*-\s*watch(?: movies and tv shows)? online for free$",
    re.IGNORECASE,
)
_OPENSUBTITLES_URL = (
    r"(?:https?://)?(?:www\.)?opensubtitles\.org(?:/\S*)?"
)
_OPENSUBTITLES_AD_RES = (
    re.compile(
        rf"^advertise your product or brand here"
        rf"(?:\s*(?::|at)\s*{_OPENSUBTITLES_URL})?$",
        re.IGNORECASE,
    ),
    re.compile(
        rf"^support us and become vip member"
        rf"(?:\s+at\s+{_OPENSUBTITLES_URL})?$",
        re.IGNORECASE,
    ),
    re.compile(
        rf"^please rate this subtitle at(?:\s+{_OPENSUBTITLES_URL})?$",
        re.IGNORECASE,
    ),
)
_OBSERVED_EXACT_ADS = frozenset({
    "subtitles downloaded from www.opensubtitles.org",
    "download movie subtitles searcher from www.opensubtitles.org",
    "downloaded from www.allsubs.org",
    "subscene.com",
    "www.titlovi.com",
    "preuzeto sa www.titlovi.com",
    (
        "created and encoded by -- bokutox -- of www.yify-torrents.com. "
        "the best 720p/1080p/3d movies with the lowest file size on the internet."
    ),
    (
        "created and encoded by -- bokutox -- of www.yify-torrents.com. "
        "the best 720p/1080p/3d movies with the lowest file size on the internet. "
        "world of warcraft - outland pvp (eu) - torporr (name)"
    ),
    "reencoded by dr.xj | psa www.psarips.com",
})


def _normalize_for_match(cue_text: str) -> str:
    """Lowercased, whitespace-collapsed cue text without markup or brackets.

    :param cue_text: Raw cue text (one cue's text lines joined by newline).
    :returns: Normalized string used only for advert-template matching.
    """
    text = _OVERRIDE_TAG_RE.sub(" ", cue_text)
    text = _MARKUP_TAG_RE.sub(" ", text)
    text = _WS_RE.sub(" ", text).strip()
    if len(text) >= 2 and text.startswith("[") and text.endswith("]"):
        text = text[1:-1].strip()
        text = _WS_RE.sub(" ", text).strip()
    return text.lower()


def is_advert_cue(cue_text: str) -> bool:
    """Is this whole cue an advert (never dialogue)?

    :param cue_text: One cue's text (without index or timestamp lines).
    :returns: True only when the entire cue matches a known advert template.
    """
    norm = _normalize_for_match(cue_text)
    if not norm:
        return False
    if _WATCH_FREE_AD_RE.fullmatch(norm):
        return True
    if any(pattern.fullmatch(norm) for pattern in _OPENSUBTITLES_AD_RES):
        return True
    return norm in _OBSERVED_EXACT_ADS


def _split_blocks(srt_text: str) -> list[str]:
    """Split SRT text on blank lines, preserving non-empty blocks.

    :param srt_text: Raw SRT file contents.
    :returns: Non-empty blocks in order.
    """
    return [b for b in re.split(r"\r?\n\s*\r?\n", srt_text.strip()) if b.strip()]


def filter_srt_text(srt_text: str) -> tuple[str, int, list[str]]:
    """Remove whole-cue adverts from SRT text and renumber survivors.

    :param srt_text: Raw SRT file contents.
    :returns: Tuple of (filtered text, removed count, removed cue variants in
        file order). Malformed input (any non-empty block without a timestamp
        line) returns the input unchanged with zero removals.
    """
    if not srt_text.strip():
        return srt_text, 0, []
    blocks = _split_blocks(srt_text)
    parsed: list[tuple[str, str]] = []
    for block in blocks:
        lines = [ln.rstrip("\r\n") for ln in block.strip().splitlines()]
        lines = [ln for ln in lines if ln.strip() != ""]
        if not lines:
            continue
        if lines[0].strip().isdigit():
            lines = lines[1:]
        if not lines or not _TIMESTAMP_LINE_RE.match(lines[0].strip()):
            return srt_text, 0, []
        timestamp = lines[0].strip()
        cue_text = "\n".join(ln.strip() for ln in lines[1:]).strip()
        parsed.append((timestamp, cue_text))
    kept: list[tuple[str, str]] = []
    removed_variants: list[str] = []
    for timestamp, cue_text in parsed:
        if cue_text and is_advert_cue(cue_text):
            removed_variants.append(cue_text)
            continue
        kept.append((timestamp, cue_text))
    if not removed_variants:
        return srt_text, 0, []
    out_lines: list[str] = []
    for idx, (timestamp, cue_text) in enumerate(kept, start=1):
        out_lines.append(str(idx))
        out_lines.append(timestamp)
        if cue_text:
            out_lines.append(cue_text)
        out_lines.append("")
    if not kept:
        return "", len(removed_variants), removed_variants
    return "\n".join(out_lines).strip() + "\n", len(removed_variants), removed_variants


def filter_srt_file(path: Path, *, dry_run: bool = False) -> dict:
    """Filter one .srt sidecar, atomically replacing it when adverts exist.

    :param path: Sidecar path (only ".srt" is processed).
    :param dry_run: When True, report without writing.
    :returns: Dict with path, removed count, variants, and wrote/would_write
        flags. Undecodable, malformed, or non-SRT inputs report zero removals
        without writing.
    """
    result: dict = {
        "path": str(path),
        "removed": 0,
        "variants": [],
        "wrote": False,
        "would_write": False,
    }
    if path.suffix.lower() != ".srt":
        return result
    try:
        original_bytes = path.read_bytes()
    except OSError as exc:
        result["error"] = str(exc)
        return result
    output_encoding = "utf-8"
    try:
        original = original_bytes.decode("utf-8-sig")
    except UnicodeDecodeError:
        try:
            from charset_normalizer import from_bytes

            candidate = from_bytes(original_bytes).best()
            if candidate is None or candidate.encoding is None or candidate.chaos > 0.10:
                raise UnicodeDecodeError("unknown", original_bytes, 0, 1,
                                         "no reliable subtitle encoding")
            original = str(candidate)
            output_encoding = candidate.encoding
        except (ImportError, UnicodeDecodeError) as exc:
            result["error"] = str(exc)
            return result
    filtered, removed, variants = filter_srt_text(original)
    result["removed"] = removed
    result["variants"] = variants
    result["would_write"] = removed > 0
    if removed == 0 or dry_run:
        return result
    try:
        original_mode = path.stat().st_mode & 0o777
    except OSError:
        original_mode = 0o644
    fd = -1
    temp_name = ""
    try:
        fd, temp_name = tempfile.mkstemp(
            prefix=f".{path.name}.", suffix=".adfilter", dir=path.parent)
        with os.fdopen(fd, "w", encoding=output_encoding, newline="\n") as handle:
            fd = -1
            handle.write(filtered)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temp_name, original_mode)
        # allow-bare-rename: publishes a filtered *.srt subtitle sidecar;
        # this function only ever operates on subtitle text, never video bytes.
        os.replace(temp_name, path)
        temp_name = ""
        result["wrote"] = True
        return result
    except OSError as exc:
        result["error"] = str(exc)
        result["wrote"] = False
        return result
    finally:
        if fd != -1:
            try:
                os.close(fd)
            except OSError:
                # allow-silent-catch: closing temp fd during error cleanup;
                # original filter/write error already recorded in result.
                pass
        if temp_name:
            try:
                os.unlink(temp_name)
            except OSError:
                # allow-silent-catch: removing temp file during error cleanup;
                # original error already recorded, leftover dotfile is harmless.
                pass
