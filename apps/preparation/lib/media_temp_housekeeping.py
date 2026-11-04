#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Detect and quarantine orphaned in-progress pipeline artifacts.

Video post-processing steps (remux, normalize, transcode, ffsubsync) write to a
temp-marked stem before atomically replacing the original. A crashed or
interrupted pass leaves that marker behind on disk (and sometimes on a stray
sidecar written against the temp stem by a concurrent process). These helpers
find and quarantine those orphans.
"""

from __future__ import annotations

import errno
import fcntl
import logging
import os
import shutil
import stat
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

if __package__ in (None, ""):
    # Run as a script by the shell gates: put tools/ on sys.path so the
    # shared enumerator imports the same way it does for every other caller.
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from lib.media_walk import iter_visible_files  # noqa: E402 - path setup must precede this import
from lib.media_temp_names import (  # noqa: E402 - script mode configures sys.path above
    TEMP_MARKERS,
    registered_temp_prefix,
    temp_artifact_target,
    temp_marker as _temp_marker,
)
from lib.sidecar_association import (  # noqa: E402 - script mode configures sys.path above
    AssociationKind,
    AssociationScope,
    SidecarAssociationError,
    classify_sidecar_association,
)
from lib.sidecar_association_io import collect_video_paths  # noqa: E402 - script mode configures sys.path above

SUBTITLE_TRANSIENT_MARKERS = TEMP_MARKERS + (".unsynced.",)
ORPHAN_JELLYFIN_METADATA_SUFFIXES = (".transcoding.nfo",
                                     ".transcoding-thumb.jpg")
VIDEO_SUFFIXES = frozenset({".mkv", ".mp4", ".m4v", ".avi", ".mov",
                            ".wmv", ".mpg", ".mpeg"})
# Encoders update output mtime as they write. Keep a generous but finite
# grace period for a live job whose output has been quiet briefly.
TEMP_RECENT_SECONDS = 30 * 60
_LSOF = "/usr/sbin/lsof"
log = logging.getLogger(__name__)


@dataclass(frozen=True)
class AmbiguousTempArtifact:
    path: Path
    competing_stems: tuple[str, ...]


@dataclass(frozen=True)
class ScopedTempInventory:
    owned: tuple[Path, ...] = ()
    ambiguous: tuple[AmbiguousTempArtifact, ...] = ()


def _original_for_temp(path: Path, marker: str) -> Path | None:
    """Return the same-extension source implied by a temp name, if any.

    Both legacy ``film.faststart.mp4`` names and the shared writer's hidden
    ``.film.subembed.RANDOM.mkv`` shape are supported. Requiring the source to
    exist is the final safety barrier against deleting a real title whose name
    merely contains a marker-like substring.
    """
    name = path.name
    if marker != _temp_marker(name):
        return None
    prefix = registered_temp_prefix(name)
    suffix = path.suffix
    if not prefix or not suffix:
        return None
    original = path.with_name(prefix + suffix)
    return original if original != path and original.is_file() else None


def _metadata_temp_prefix(path: Path) -> str | None:
    """Return a marker-bearing media stem for a recognized Jellyfin sidecar."""
    if path.name.endswith("-thumb.jpg"):
        media_name = path.name[:-len("-thumb.jpg")]
    elif path.suffix.lower() in {".nfo", ".jpg", ".png"}:
        media_name = path.name[:-len(path.suffix)]
    else:
        return None
    marker = _temp_marker(media_name + ".")
    if marker is None or not media_name.endswith(marker.rstrip(".")):
        return None
    return media_name


def _has_temp_media_for_sidecar(path: Path, temp_prefix: str) -> bool:
    return any((path.parent / f"{temp_prefix}{suffix}").is_file()
               for suffix in VIDEO_SUFFIXES)


def _age_seconds(path: Path, now: float) -> float:
    try:
        return max(0.0, now - path.stat().st_mtime)
    except OSError as exc:
        log.warning("could not stat temp artifact %s; retaining conservatively: %s",
                    path, exc)
        return 0.0


def _is_open_by_process(path: Path) -> tuple[bool, str]:
    """Check process ownership; uncertainty fails closed (keeps the artifact)."""
    try:
        result = subprocess.run(
            [_LSOF, "-t", str(path)], capture_output=True, text=True,
            timeout=5, check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return True, "lsof unavailable/timeout; retained conservatively"
    if result.returncode == 0 and result.stdout.strip():
        return True, f"open by pid(s) {','.join(result.stdout.split())}"
    if result.stderr.strip():
        return True, f"lsof uncertain: {result.stderr.strip()}; retained conservatively"
    if result.returncode not in (0, 1):
        return True, f"lsof error rc={result.returncode}; retained conservatively"
    return False, "not open by a process"


def _try_owner_lock(original: Path) -> tuple[bool, int | None]:
    """Return whether the target writer lock is held, retaining our own fd."""
    lock_path = original.with_name(f".{original.name}.write.lock")
    if not lock_path.exists():
        return False, None
    descriptor: int | None = None
    try:
        descriptor = os.open(lock_path, os.O_RDWR)
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as exc:
        if descriptor is not None:
            try:
                os.close(descriptor)
            except OSError as close_error:
                log.warning("could not close temp owner lock %s after lock error %s: %s",
                            lock_path, exc, close_error)
        if exc.errno in (errno.EACCES, errno.EAGAIN, errno.EWOULDBLOCK):
            return True, None
        log.warning("could not inspect temp owner lock %s; retaining conservatively: %s",
                    lock_path, exc)
        return True, None
    return False, descriptor


def _release_owner_lock(descriptor: int | None) -> None:
    if descriptor is None:
        return
    try:
        fcntl.flock(descriptor, fcntl.LOCK_UN)
    finally:
        os.close(descriptor)


def temp_liveness(path: Path, *, now: float | None = None,
                  recent_seconds: float = TEMP_RECENT_SECONDS) -> tuple[bool, str]:
    """Return (live, evidence) for one source-backed registered media temp."""
    timestamp = time.time() if now is None else now
    age = _age_seconds(path, timestamp)
    if age <= recent_seconds:
        return True, f"recent mtime ({age:.0f}s old)"
    opened, evidence = _is_open_by_process(path)
    return (True, evidence) if opened else (False, f"stale: {age:.0f}s old; {evidence}")


def find_live_temps(roots: list[Path], *, now: float | None = None,
                    recent_seconds: float = TEMP_RECENT_SECONDS) -> list[Path]:
    """Return registered media temps which are live and source-backed."""
    live: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.is_symlink() or not path.is_file():
                continue
            marker = _temp_marker(path.name)
            original = _original_for_temp(path, marker) if marker is not None else None
            if original is None:
                continue
            owner_busy, owner_fd = _try_owner_lock(original)
            try:
                if owner_busy or temp_liveness(
                        path, now=now, recent_seconds=recent_seconds)[0]:
                    live.append(path)
            finally:
                _release_owner_lock(owner_fd)
    return live


def _temp_stem_path(path: Path) -> Path | None:
    """Return ``<dir>/<stem>`` of the library file a marked temp belongs to."""
    marker = _temp_marker(path.name)
    if marker is None or path.suffix.lower() not in VIDEO_SUFFIXES:
        return None
    prefix = path.name[:path.name.index(marker)].lstrip(".")
    return path.parent / prefix if prefix else None


def find_held_stems(roots: list[Path], *, now: float | None = None,
                    recent_seconds: float = TEMP_RECENT_SECONDS) -> set[Path]:
    """Return ``<dir>/<stem>`` for every library file a live writer holds.

    A file is held only while its own producer is live: a marked temp that is
    recent or open by a process, or a same-stem sibling whose writer lock is
    held. A stale leftover holds nothing, and a live temp for one file never
    holds any other file (c299: the old whole-pass deferral starved subtitle
    fetching for months behind one long batch).
    """
    held: set[Path] = set()
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.is_symlink() or not path.is_file():
                continue
            stem_path = _temp_stem_path(path)
            if stem_path is None or stem_path in held:
                continue
            siblings = [stem_path.with_name(stem_path.name + suffix)
                        for suffix in sorted(VIDEO_SUFFIXES)]
            busy = False
            for sibling in siblings:
                if not sibling.is_file():
                    continue
                owner_busy, owner_fd = _try_owner_lock(sibling)
                _release_owner_lock(owner_fd)
                busy = busy or owner_busy
            if busy or temp_liveness(path, now=now,
                                     recent_seconds=recent_seconds)[0]:
                held.add(stem_path)
    return held


def eligible_videos(roots: list[Path], *, now: float | None = None) -> list[Path]:
    """Return the library videos a subtitle pass may touch, sorted.

    Excludes hidden files and everything inside hidden directories, anything whose path carries a registered temp
    marker, and every file a live writer currently holds (per-file, so one
    busy title never defers the rest of the library).
    """
    held = find_held_stems(roots, now=now)
    videos: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        for path in iter_visible_files(root):
            if path.suffix.lower() not in VIDEO_SUFFIXES:
                continue
            if path.is_symlink():
                continue
            if any(marker in str(path) for marker in TEMP_MARKERS):
                continue
            if path.with_suffix("") in held:
                continue
            videos.append(path)
    return sorted(videos)


def _cli(argv: list[str]) -> int:
    """Small shell bridge so shell gates consume this module's registry."""
    if not argv or argv[0] not in {"markers", "gate", "reap", "eligible"}:
        print("usage: media_temp_housekeeping.py {markers|gate|reap|eligible} [ROOT ...]",
              file=sys.stderr)
        return 2
    action, *values = argv
    if action == "markers":
        print("\n".join(TEMP_MARKERS))
        return 0
    roots = [Path(value) for value in values]
    if not roots:
        print(f"{action} requires at least one root", file=sys.stderr)
        return 2
    import logging

    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    log = logging.getLogger("media-temp-housekeeping")
    if action == "reap":
        count = reap_stale_temps(roots, log)
        log.info("pipeline temp reaper pass complete: removed=%d roots=%s", count,
                 ",".join(str(root) for root in roots))
        return 0
    if action == "eligible":
        held = find_held_stems(roots)
        for stem_path in sorted(held):
            print(f"subtitle pass skips file held by a live writer: {stem_path}",
                  file=sys.stderr)
        for path in eligible_videos(roots):
            print(path)
        return 0
    live = find_live_temps(roots)
    for path in live:
        log.warning("live pipeline temp blocks work: %s", path)
    return 0 if live else 1


def reap_stale_temps(roots: list[Path], log, *, now: float | None = None,
                     recent_seconds: float = TEMP_RECENT_SECONDS) -> int:
    """Remove stale source-backed temps and orphan Jellyfin sidecars.

    Media temps are unlinked only when their non-temp original exists. Known
    Jellyfin sidecars are removed only when no corresponding marked video temp
    remains. Every removal is logged with path, size, age and reason.
    """
    timestamp = time.time() if now is None else now
    removed = 0
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.is_symlink() or not path.is_file():
                continue
            try:
                stat = path.stat()
                age = max(0.0, timestamp - stat.st_mtime)
                size = stat.st_size
            except OSError:
                continue
            temp_prefix = _metadata_temp_prefix(path)
            if temp_prefix is not None and not _has_temp_media_for_sidecar(path, temp_prefix):
                reason = "orphan Jellyfin sidecar: corresponding temp media is absent"
            else:
                marker = _temp_marker(path.name)
                original = _original_for_temp(path, marker) if marker is not None else None
                if original is None:
                    continue
                owner_busy, owner_fd = _try_owner_lock(original)
                try:
                    if owner_busy:
                        continue
                    live, evidence = temp_liveness(
                        path, now=timestamp, recent_seconds=recent_seconds)
                    if live:
                        continue
                    reason = evidence
                    path.unlink()
                    log.warning("reaped stale pipeline artifact path=%s size=%d age=%.0fs reason=%s",
                                path, size, age, reason)
                    removed += 1
                except OSError as exc:
                    log.warning("could not reap stale pipeline artifact path=%s size=%d age=%.0fs reason=%s error=%s",
                                path, size, age, reason, exc)
                finally:
                    _release_owner_lock(owner_fd)
                continue
            try:
                path.unlink()
                log.warning("reaped stale pipeline artifact path=%s size=%d age=%.0fs reason=%s",
                            path, size, age, reason)
                removed += 1
            except OSError as exc:
                log.warning("could not reap stale pipeline artifact path=%s size=%d age=%.0fs reason=%s error=%s",
                            path, size, age, reason, exc)
    return removed


def quarantine_orphan_subs(roots: list[Path], quarantine_base: Path, log) -> int:
    """Move subtitles carrying any known in-progress writer marker.

    Files land in <quarantine_base>/orphan-subs-<date>/ before processing begins.

    These are produced by an as-yet-unidentified race between the in-flight
    pipeline and a concurrent process saving subs against a temp video stem.
    They can confuse Jellyfin (it sees them as additional subtitle tracks),
    so we sweep them out at the start of every run rather than wait for a
    manual cleanup.  Move (not delete) so the user can inspect what was
    swept if anything looks wrong.
    """
    quarantine_root = quarantine_base / f"orphan-subs-{time.strftime('%Y%m%d')}"
    moved = 0
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*.srt"):
            if not any(m in path.name for m in TEMP_MARKERS):
                continue
            try:
                quarantine_root.mkdir(parents=True, exist_ok=True)
                # Preserve some context: include the parent folder name.
                parent_dir = quarantine_root / path.parent.name
                parent_dir.mkdir(parents=True, exist_ok=True)
                # allow-bare-rename: quarantine_orphan_subs: quarantines
                # orphaned *.srt subtitle sidecars produced by a race between
                # the in-flight pipeline and a concurrent subtitle save against
                # a temp video stem; the move target is always a .srt sidecar,
                # never a video file
                shutil.move(str(path), str(parent_dir / path.name))
                moved += 1
            except Exception as exc:
                log.warning("could not quarantine %s: %s", path, exc)
    if moved:
        log.info("quarantined %d orphan subtitle file(s) to %s", moved, quarantine_root)
    return moved


def quarantine_orphan_jellyfin_metadata(roots: list[Path],
                                        quarantine_base: Path, log) -> int:
    """Move metadata sidecars created for legacy visible transcode temps.

    Older transcodes used ``<stem>.transcoding.mp4``, which Jellyfin indexed
    during an encode and decorated with matching NFO and thumbnail files.
    The final video rename cannot carry those generated sidecars because they
    appear asynchronously. Quarantine exact known suffixes after a backfill
    run, preserving root-relative paths and bytes for inspection while leaving
    ordinary NFO and artwork files untouched.
    """
    quarantine_root = quarantine_base / f"orphan-metadata-{time.strftime('%Y%m%d')}"
    moved = 0
    for root_index, root in enumerate(roots):
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if not path.is_file():
                continue
            if not path.name.endswith(ORPHAN_JELLYFIN_METADATA_SUFFIXES):
                continue
            try:
                destination = (quarantine_root / f"{root_index}-{root.name}"
                               / path.relative_to(root))
                destination.parent.mkdir(parents=True, exist_ok=True)
                if destination.exists():
                    for duplicate_index in range(1, 1000):
                        candidate = destination.with_name(
                            f"{destination.stem}__dup{duplicate_index}{destination.suffix}")
                        if not candidate.exists():
                            destination = candidate
                            break
                    else:
                        log.warning("could not quarantine %s: duplicate limit reached", path)
                        continue
                # allow-bare-rename: quarantine_orphan_jellyfin_metadata:
                # quarantines *.transcoding.nfo / *.transcoding-thumb.jpg
                # Jellyfin metadata sidecars left behind by a race;
                # ORPHAN_JELLYFIN_METADATA_SUFFIXES names only metadata
                # extensions, never a video file
                shutil.move(str(path), str(destination))
                moved += 1
            except Exception as exc:
                log.warning("could not quarantine %s: %s", path, exc)
    if moved:
        log.info("quarantined %d orphan Jellyfin metadata file(s) to %s",
                 moved, quarantine_root)
    return moved


def find_leftover_temps(roots: list[Path]) -> list[Path]:
    """Return files matching the in-progress temp-file markers.

    These indicate ffmpeg processes that died mid-flight or sub-prefetch runs
    that wrote to the wrong stem.  The caller surfaces them in the completion
    alert so the user can decide whether to clean them up.
    """
    found: list[Path] = []
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if not path.is_file():
                continue
            if any(marker in path.name for marker in TEMP_MARKERS):
                found.append(path)
    return found


def find_leftover_temps_for_scopes(
        scopes: list[AssociationScope]) -> ScopedTempInventory:
    """Find artifacts for independent selected families and true aliases.

    Each scope is one original selected family. Source/output aliases belong
    together only when the caller has an observed conversion mapping; separate
    ``--path`` selections remain competitors. A candidate is reported
    ambiguous if any selected family cannot exclusively claim it.
    """
    if not scopes:
        return ScopedTempInventory()

    scope_videos = [(scope, collect_video_paths(scope)) for scope in scopes]
    parents: dict[Path, Path] = {}
    for scope in scopes:
        for path in scope.videos:
            parents.setdefault(path.parent, path)

    owned: set[Path] = set()
    ambiguous: dict[Path, set[str]] = {}
    for parent, selected_path in parents.items():
        try:
            entries = sorted(parent.iterdir(),
                             key=lambda path: (path.name.casefold(), path.name))
        except OSError as exc:
            raise SidecarAssociationError(
                selected_path=selected_path, candidate=parent,
                reason=f"directory listing failed: {exc}",
            ) from exc
        for candidate in entries:
            target_name = temp_artifact_target(candidate.name)
            try:
                candidate_stat = candidate.lstat()
            except OSError as exc:
                raise SidecarAssociationError(
                    selected_path=selected_path, candidate=candidate,
                    reason=f"lstat failed: {exc}",
                ) from exc
            if stat.S_ISLNK(candidate_stat.st_mode):
                if target_name is not None:
                    raise SidecarAssociationError(
                        selected_path=selected_path, candidate=candidate,
                        reason="temp artifact is a symlink; refusing to follow it",
                    )
                continue
            if not stat.S_ISREG(candidate_stat.st_mode) or target_name is None:
                continue

            target = candidate.with_name(target_name)
            decisions = [classify_sidecar_association(scope, target, videos)
                         for scope, videos in scope_videos]
            selected_decisions = [decision for decision in decisions
                                  if decision.kind is not AssociationKind.UNRELATED]
            ambiguous_decisions = [decision for decision in selected_decisions
                                   if decision.kind is AssociationKind.AMBIGUOUS]
            if ambiguous_decisions:
                stems = {stem for decision in ambiguous_decisions
                         for stem in decision.competing_stems}
                ambiguous[candidate] = stems
            elif selected_decisions:
                owned.add(candidate)

    for candidate, stems in ambiguous.items():
        log.warning(
            "ambiguous scoped temp artifact; not attributed to selected "
            "path and left untouched: %s; competing stems=%s",
            candidate, ", ".join(sorted(stems, key=str.casefold)),
        )
    return ScopedTempInventory(
        owned=tuple(sorted(owned)),
        ambiguous=tuple(
            AmbiguousTempArtifact(path=path, competing_stems=tuple(sorted(
                stems, key=lambda stem: (stem.casefold(), stem))))
            for path, stems in sorted(ambiguous.items(), key=lambda item: str(item[0]))
        ),
    )


def find_leftover_temps_for_paths(paths: list[Path]) -> ScopedTempInventory:
    """Compatibility wrapper treating each path as an independent selection."""
    return find_leftover_temps_for_scopes(
        [AssociationScope(path) for path in paths]
    )


if __name__ == "__main__":
    raise SystemExit(_cli(sys.argv[1:]))
