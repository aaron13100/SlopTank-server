# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Pure decisions about which video family an adjacent sidecar belongs to.

This module deliberately performs no filesystem reads. Callers that need a
current neighbor snapshot use :mod:`lib.sidecar_association_io` and keep the
result scoped to that snapshot; mutation callers must still revalidate under
their publication lock.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from pathlib import Path


@dataclass(frozen=True)
class AssociationScope:
    """One selected video and any source/output paths representing it."""

    selected_video: Path
    aliases: tuple[Path, ...] = ()

    @property
    def videos(self) -> tuple[Path, ...]:
        result: list[Path] = []
        seen: set[Path] = set()
        for path in (self.selected_video, *self.aliases):
            if path not in seen:
                seen.add(path)
                result.append(path)
        return tuple(result)


class AssociationKind(str, Enum):
    OWNED = "owned"
    UNRELATED = "unrelated"
    AMBIGUOUS = "ambiguous"


@dataclass(frozen=True)
class AssociationDecision:
    candidate: Path
    kind: AssociationKind
    matched_aliases: tuple[Path, ...] = ()
    competing_stems: tuple[str, ...] = ()


def _name_matches(candidate_stem: str, video_stem: str) -> bool:
    candidate = candidate_stem.casefold()
    video = video_stem.casefold()
    return candidate == video or candidate.startswith(video + ".")


def classify_sidecar_association(
        scope: AssociationScope,
        candidate: Path,
        neighboring_videos: tuple[Path, ...] | list[Path]) -> AssociationDecision:
    """Classify ``candidate`` using same-parent video names in a snapshot.

    A final extension is removed from both the candidate and video paths.
    Exact case-insensitive stems and arbitrary dot-separated labels belong to
    a video stem. All dot-boundary matches are considered: if two distinct
    video families match, longest-prefix preference is unsafe and the result
    is ambiguous.
    """
    aliases = tuple(path for path in scope.videos
                    if path.parent == candidate.parent)
    alias_stems = {path.stem.casefold() for path in aliases}

    # The selected aliases form one family even when a conversion changed the
    # stem. Other videos with case-fold-identical stems form their own family.
    selected_family = ("selected", "")
    family_stems: dict[tuple[str, str], set[str]] = {}
    family_matched_aliases: dict[tuple[str, str], set[Path]] = {}

    for alias in aliases:
        if _name_matches(candidate.stem, alias.stem):
            family_stems.setdefault(selected_family, set()).add(alias.stem)
            family_matched_aliases.setdefault(selected_family, set()).add(alias)

    seen_videos: set[Path] = set()
    for video in neighboring_videos:
        if video.parent != candidate.parent or video in seen_videos:
            continue
        seen_videos.add(video)
        if not _name_matches(candidate.stem, video.stem):
            continue
        family = (selected_family if video.stem.casefold() in alias_stems
                  else ("neighbor", video.stem.casefold()))
        family_stems.setdefault(family, set()).add(video.stem)

    matching_families = list(family_stems)
    matching_aliases = tuple(sorted(
        family_matched_aliases.get(selected_family, set()),
        key=lambda path: (str(path.parent).casefold(), path.name.casefold(),
                          path.name),
    ))
    competing_stems = tuple(sorted(
        {stem for stems in family_stems.values() for stem in stems},
        key=lambda stem: (stem.casefold(), stem),
    ))

    if selected_family in family_stems and len(matching_families) > 1:
        kind = AssociationKind.AMBIGUOUS
    elif matching_families == [selected_family]:
        kind = AssociationKind.OWNED
    else:
        kind = AssociationKind.UNRELATED

    return AssociationDecision(
        candidate=candidate,
        kind=kind,
        matched_aliases=matching_aliases,
        competing_stems=competing_stems,
    )


class SidecarAssociationError(RuntimeError):
    """A current filesystem snapshot could not safely classify a sidecar."""

    def __init__(self, *, selected_path: Path, candidate: Path | None,
                 competing_stems: tuple[str, ...] = (), reason: str):
        self.selected_path = selected_path
        self.candidate = candidate
        self.competing_stems = competing_stems
        self.reason = reason
        super().__init__(
            f"sidecar association unavailable for selected={selected_path} "
            f"candidate={candidate}: {reason}; "
            f"competing_stems={list(competing_stems)!r}"
        )
