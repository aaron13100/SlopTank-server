# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Filesystem snapshots for the pure sidecar association policy."""

from __future__ import annotations

import os
import stat
from pathlib import Path

from lib.media_temp_names import is_registered_temp_video_name
from lib.organize_media_config import VIDEO_EXTS
from lib.sidecar_association import (
    AssociationDecision,
    AssociationScope,
    SidecarAssociationError,
    classify_sidecar_association,
)

_VIDEO_EXTENSIONS = frozenset(extension.casefold() for extension in VIDEO_EXTS)


def _scope_paths(scope: AssociationScope) -> tuple[Path, ...]:
    return scope.videos


def _lstat(path: Path, *, selected_path: Path) -> os.stat_result:
    try:
        return path.lstat()
    except OSError as exc:
        raise SidecarAssociationError(
            selected_path=selected_path,
            candidate=path,
            reason=f"lstat failed: {exc}",
        ) from exc


def collect_video_paths(scope: AssociationScope) -> tuple[Path, ...]:
    """List actual neighboring videos in each selected alias's immediate parent.

    Directories are not recursively traversed. Symlink video candidates and
    listing/stat errors fail closed through :class:`SidecarAssociationError`.
    Registered temp video names are excluded, while marker-like ordinary title
    names remain part of the neighbor set.
    """
    selected_path = scope.selected_video
    parents: dict[Path, Path] = {}
    for path in _scope_paths(scope):
        parents.setdefault(path.parent, path)

    videos: list[Path] = []
    for parent, parent_selection in parents.items():
        parent_stat = _lstat(parent, selected_path=parent_selection)
        if stat.S_ISLNK(parent_stat.st_mode):
            raise SidecarAssociationError(
                selected_path=parent_selection, candidate=parent,
                reason="selected parent is a symlink; refusing to follow it",
            )
        if not stat.S_ISDIR(parent_stat.st_mode):
            raise SidecarAssociationError(
                selected_path=parent_selection, candidate=parent,
                reason="selected parent is not a directory",
            )
        try:
            entries = sorted(parent.iterdir(), key=lambda path: (path.name.casefold(), path.name))
        except OSError as exc:
            raise SidecarAssociationError(
                selected_path=parent_selection, candidate=parent,
                reason=f"directory listing failed: {exc}",
            ) from exc

        for entry in entries:
            entry_stat = _lstat(entry, selected_path=parent_selection)
            if stat.S_ISLNK(entry_stat.st_mode):
                if (entry.suffix.casefold() in _VIDEO_EXTENSIONS
                        and not is_registered_temp_video_name(entry.name, VIDEO_EXTS)):
                    raise SidecarAssociationError(
                        selected_path=parent_selection, candidate=entry,
                        reason="neighbor video candidate is a symlink; refusing to follow it",
                    )
                continue
            if not stat.S_ISREG(entry_stat.st_mode):
                continue
            if entry.suffix.casefold() not in _VIDEO_EXTENSIONS:
                continue
            if is_registered_temp_video_name(entry.name, VIDEO_EXTS):
                continue
            videos.append(entry)

    return tuple(videos)


def classify_existing_sidecar(
        scope: AssociationScope, candidate: Path) -> AssociationDecision:
    """Classify one existing non-symlink candidate against a fresh local listing."""
    parent_stat = _lstat(candidate.parent, selected_path=scope.selected_video)
    if stat.S_ISLNK(parent_stat.st_mode):
        raise SidecarAssociationError(
            selected_path=scope.selected_video, candidate=candidate.parent,
            reason="candidate parent is a symlink; refusing to follow it",
        )
    if not stat.S_ISDIR(parent_stat.st_mode):
        raise SidecarAssociationError(
            selected_path=scope.selected_video, candidate=candidate.parent,
            reason="candidate parent is not a directory",
        )
    candidate_stat = _lstat(candidate, selected_path=scope.selected_video)
    if stat.S_ISLNK(candidate_stat.st_mode):
        raise SidecarAssociationError(
            selected_path=scope.selected_video, candidate=candidate,
            reason="candidate is a symlink; refusing to follow it",
        )
    if not stat.S_ISREG(candidate_stat.st_mode):
        raise SidecarAssociationError(
            selected_path=scope.selected_video, candidate=candidate,
            reason="candidate is not a regular file",
        )
    neighboring_videos = collect_video_paths(scope)
    return classify_sidecar_association(scope, candidate, neighboring_videos)
