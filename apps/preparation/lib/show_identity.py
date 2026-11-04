# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Filesystem and provider identity for established TV shows."""

from __future__ import annotations

import unicodedata
import xml.etree.ElementTree as ET
from pathlib import Path

from lib.organize_media_config import DOWNLOADS_ROOT, MEDIA_ROOT, MOVIES_ROOT, TV_ROOT, VIDEO_EXTS
from lib.media_walk import iter_visible_files
from lib.provider_identity import (
    _PROVIDER_TAGS, _clean_provider_ids, _id_groups, _ids_match,
    merge_provider_ids, provider_ids_from_text,
)


def _show_name_from_folder(file_path: Path) -> str | None:
    """Try to extract a show name from the parent folder using guessit.

    Imports guessit here rather than at module level: importing it is a
    real, measured cost (loads its rebulk pattern rules), and this module is
    loaded unconditionally by every backfill-streaming.py invocation,
    including a --dry-run over a root with no matching files -- which never
    calls this function. See queue task c230.
    """
    from guessit import guessit
    folder = file_path.parent
    # Skip library roots
    if folder in (MOVIES_ROOT, TV_ROOT, MEDIA_ROOT, DOWNLOADS_ROOT):
        return None
    folder_info = guessit(folder.name, {"type": "episode"})
    title = folder_info.get("title")
    if title and folder_info.get("type") == "episode":
        return title
    return None


def _normalize_show_identity(value: str) -> str:
    """Return a punctuation-insensitive key for a show title."""
    value = unicodedata.normalize("NFKC", value or "").casefold()
    return "".join(ch for ch in value if ch.isalnum())


def _show_nfo_metadata(show_dir: Path) -> tuple[set[str], dict[str, str], int | None]:
    """Read aliases, provider IDs, and year from a Jellyfin/Kodi tvshow.nfo."""
    nfo = show_dir / "tvshow.nfo"
    if not nfo.is_file():
        return set(), {}, None
    try:
        root = ET.parse(nfo).getroot()
    except (ET.ParseError, OSError):
        return set(), {}, None
    aliases = set()
    for tag in ("title", "originaltitle"):
        value = root.findtext(tag)
        if value and value.strip():
            aliases.add(value.strip())
    provider_ids = {}
    for element in root.iter():
        tag = element.tag.rsplit("}", 1)[-1].casefold()
        value = (element.text or "").strip()
        provider = _PROVIDER_TAGS.get(tag)
        if provider and value:
            provider_ids[provider] = value
        if tag == "uniqueid" and value:
            provider = _PROVIDER_TAGS.get((element.attrib.get("type") or "").casefold())
            if provider:
                provider_ids[provider] = value
    year = None
    try:
        year_text = root.findtext("year")
        if year_text:
            year = int(year_text)
    except (TypeError, ValueError):
        year = None
    return aliases, _clean_provider_ids(provider_ids), year


def _show_video_count(show_dir: Path) -> int:
    """Count established episodes without depending on Jellyfin's database."""
    try:
        return sum(
            1
            for path in iter_visible_files(show_dir)
            if path.suffix.lower() in VIDEO_EXTS
        )
    except OSError:
        return 0


def _provider_ids_from_filename(path: Path) -> dict[str, str]:
    """Parse Jellyfin-style provider IDs embedded in a release path."""
    return provider_ids_from_text(path)


def _incoming_show_provider_ids(file_path: Path | None) -> dict[str, str]:
    """Get authoritative series IDs from the release path or pack tvshow.nfo."""
    if file_path is None:
        return {}
    for parent in file_path.parents:
        # A provider tag on a containing show/pack directory describes the
        # series. Do not consume tags from the episode filename itself: those
        # may be episode IDs rather than series IDs.
        provider_ids = _provider_ids_from_filename(Path(parent.name))
        if provider_ids:
            return provider_ids
        _, nfo_ids, _ = _show_nfo_metadata(parent)
        if nfo_ids:
            return nfo_ids
        if parent in {DOWNLOADS_ROOT, MEDIA_ROOT, TV_ROOT}:
            break
    return {}


def _show_identities() -> list[dict]:
    identities = []
    if not TV_ROOT.is_dir():
        return identities
    try:
        children = [child for child in TV_ROOT.iterdir() if child.is_dir()]
    except OSError:
        return identities
    for child in children:
        aliases, provider_ids, year = _show_nfo_metadata(child)
        # Jellyfin reads provider tags directly from folder names before an
        # NFO exists, so the organizer must recognize the same durable hint.
        provider_ids, identity_conflict = merge_provider_ids(
            _provider_ids_from_filename(Path(child.name)), provider_ids)
        aliases.add(child.name)
        identities.append({
            "path": child,
            "aliases": aliases,
            "ids": provider_ids,
            "year": year,
            "videos": _show_video_count(child),
            "identity_conflict": identity_conflict,
        })
    return identities


def _best_established(identities: list[dict]) -> Path:
    ranked = sorted(
        identities,
        key=lambda identity: (
            -identity["videos"],
            -len(identity["ids"]),
            identity["path"].name.casefold(),
        ),
    )
    return ranked[0]["path"]
