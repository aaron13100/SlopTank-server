# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Registered media temp naming rules shared by discovery and cleanup code."""

from __future__ import annotations

import re
from pathlib import Path


TEMP_MARKERS = (".transcoding.", ".faststart.", ".remuxstream.",
                ".normalizing.", ".syncing.", ".remuxing.",
                ".tvaudio-add.", ".audio-defaulting.", ".audio-reorder.",
                ".audiofix.", ".subembed.")


def temp_marker(name: str) -> str | None:
    matches = [marker for marker in TEMP_MARKERS if marker in name]
    return min(matches, key=name.index) if matches else None


def registered_temp_prefix(name: str) -> str | None:
    """Return the source-like prefix only for known writer name shapes.

    Supports legacy ``film.faststart.mp4`` / ``film.transcoding.nfo`` names
    and hidden random ``.film.syncing.RANDOM.srt`` names. A marker-like title
    such as ``The.faststart.saga.mp4`` is not a temp because it has neither
    recognized shape.
    """
    marker = temp_marker(name)
    suffix = Path(name).suffix
    if marker is None or not suffix:
        return None
    marker_at = name.index(marker)
    prefix = name[:marker_at].lstrip(".")
    tail = name[marker_at + len(marker):]
    random_part = tail[:-len(suffix)] if tail.endswith(suffix) else ""
    deterministic_shape = tail.casefold() == suffix.lstrip(".").casefold()
    hidden_random_shape = (
        name.startswith(".") and len(random_part) >= 8
        and re.fullmatch(r"[A-Za-z0-9_-]+", random_part) is not None
    )
    if not prefix or not (deterministic_shape or hidden_random_shape):
        return None
    return prefix


def is_registered_temp_video_name(name: str,
                                  video_extensions: set[str] | frozenset[str]) -> bool:
    """Whether ``name`` is a registered hidden-random or legacy video temp."""
    return (Path(name).suffix.casefold() in
            {extension.casefold() for extension in video_extensions}
            and registered_temp_prefix(name) is not None)


def temp_artifact_target(name: str) -> str | None:
    """Return the intended sibling filename for one recognized temp artifact."""
    # Jellyfin's legacy thumbnail spelling is special: it uses ``-thumb``
    # after the temp marker rather than the normal marker + extension shape.
    thumb_suffix = ".transcoding-thumb.jpg"
    if name.endswith(thumb_suffix):
        prefix = name[:-len(thumb_suffix)].lstrip(".")
        return f"{prefix}.jpg" if prefix else None

    prefix = registered_temp_prefix(name)
    if prefix is None:
        return None
    suffix = Path(name).suffix
    return f"{prefix}{suffix}"
