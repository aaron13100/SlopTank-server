#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Portable, safe replacement and relocation of library media files.

Permalink identity belongs to the existing library metadata and item records.
This module only publishes files atomically and keeps displaced originals in
the configured undo window; it does not read or write filesystem attributes,
contact a permalink service, or decide which library item a path represents.
"""

from __future__ import annotations

import errno
import logging
import os
import shutil
import tempfile
from pathlib import Path

from . import media_preparation_host, retirement_ledger
from .organize_media_config import ROOT

log = logging.getLogger(__name__)

DEFAULT_RETIRED_DIR = ROOT / "retired-originals"


class MediaIdentityError(Exception):
    """A media file could not be installed safely."""


def default_retired_dir() -> Path:
    """Resolve the configured directory for replaced originals."""
    override = os.environ.get("SLOPTANK_RETIRED_DIR", "")
    if override:
        return Path(override)
    if os.environ.get("SLOPTANK_TEST_MODE", ""):
        raise MediaIdentityError(
            "a test run reached the production retirement directory resolver "
            "without SLOPTANK_RETIRED_DIR set; inject a retired_dir or set "
            "SLOPTANK_RETIRED_DIR to an isolated directory")
    return DEFAULT_RETIRED_DIR


def move_media_file(src: Path, dst: Path) -> Path:
    """Move a file to a new path without overwriting existing content.

    Same-filesystem moves use an atomic rename. Cross-filesystem moves copy to
    a temporary file beside the destination, verify its size, fsync it, then
    atomically publish it before removing the source.
    """
    if dst.exists():
        raise MediaIdentityError(f"refusing to move {src} onto existing {dst}")
    try:
        os.rename(src, dst)
        return dst
    except OSError as exc:
        if exc.errno != errno.EXDEV:
            raise
    fd, temp_name = tempfile.mkstemp(prefix=f".{dst.name}.moving-", dir=dst.parent)
    os.close(fd)
    candidate = Path(temp_name)
    try:
        shutil.copy2(src, candidate)
        if candidate.stat().st_size != src.stat().st_size:
            raise OSError(errno.EIO, f"copy of {src} changed size")
        with candidate.open("rb") as stream:
            os.fsync(stream.fileno())
        os.link(candidate, dst)
        candidate.unlink()
    except Exception:
        candidate.unlink(missing_ok=True)
        raise
    src.unlink()
    return dst


def retirement_slot(retired_dir: Path, name: str) -> Path:
    """Return an unused path in the retirement directory."""
    retired_dir.mkdir(parents=True, exist_ok=True)
    slot = retired_dir / name
    counter = 2
    base = Path(name)
    while slot.exists():
        slot = retired_dir / f"{base.stem}.{counter}{base.suffix}"
        counter += 1
    return slot


def copy_retirement_across_devices(source: Path, slot: Path) -> None:
    """Durably copy a displaced file across filesystems and publish it."""
    fd, temp_name = tempfile.mkstemp(
        prefix=f".{slot.name}.retiring-", dir=slot.parent)
    os.close(fd)
    candidate = Path(temp_name)
    try:
        source_size = source.stat().st_size
        shutil.copy2(source, candidate)
        if candidate.stat().st_size != source_size:
            raise OSError(errno.EIO, "retirement copy changed size")
        with candidate.open("rb") as stream:
            os.fsync(stream.fileno())
        os.link(candidate, slot)
        candidate.unlink()
        if slot.stat().st_size != source_size:
            raise OSError(errno.EIO, "published retirement copy changed size")
    except Exception:
        try:
            slot.unlink(missing_ok=True)
        except OSError as cleanup_error:
            log.warning("could not remove rejected retirement copy %s: %s",
                        slot, cleanup_error)
        raise
    finally:
        try:
            candidate.unlink(missing_ok=True)
        except OSError as cleanup_error:
            log.warning("could not remove retirement temp %s: %s",
                        candidate, cleanup_error)


def _retire(original: Path, retired_dir: Path) -> Path:
    """Retain the original before allowing its library path to be overwritten."""
    slot = None
    try:
        slot = retirement_slot(retired_dir, original.name)
        os.link(original, slot)
    except OSError as exc:
        if exc.errno == errno.EXDEV and slot is not None:
            try:
                copy_retirement_across_devices(original, slot)
            except OSError as copy_error:
                raise MediaIdentityError(
                    f"cannot retain original {original} in {retired_dir}; "
                    f"replacement refused: {copy_error}") from copy_error
        else:
            raise MediaIdentityError(
                f"cannot retain original {original} in {retired_dir}; "
                f"replacement refused: {exc}") from exc
    return slot


def _remove_orphaned_appledouble(original: Path) -> None:
    """Best-effort cleanup of a macOS AppleDouble stub for a retired path."""
    stub = original.parent / f"._{original.name}"
    try:
        stub.unlink()
    except FileNotFoundError:
        return
    except OSError as exc:
        log.warning("could not remove orphaned AppleDouble stub %s: %s", stub, exc)


def replace_media_file(tmp: Path, original: Path,
                       target: Path | None = None,
                       retired_dir: Path | None = None) -> Path:
    """Atomically install ``tmp`` at the library path and retire old content.

    When ``target`` is the original path, the old file is hardlinked into the
    undo window before the atomic replacement. For a new target, the complete
    output is published first and the old path is then moved into retirement.
    An in-place replacement is refused if the original cannot be retained.
    A bundled preparation worker delegates publication
    to its existing parent-owned host so the server retains its item record.
    Other callers notify the library through its normal update or scan mechanism.
    """
    host = media_preparation_host.current_preparation_host()
    if host is not None:
        return host.replace(
            tmp, original, target, item_id=host.preflight(original))
    retired_dir = retired_dir or default_retired_dir()
    destination = target or original
    if destination != original and destination.exists():
        raise MediaIdentityError(
            f"refusing to replace {destination}, which already exists and is "
            f"not the source {original}")
    if destination == original:
        slot = _retire(original, retired_dir)
        try:
            tmp.replace(original)
        except Exception:
            slot.unlink(missing_ok=True)
            raise
        retirement_ledger.record_retirement(retired_dir, slot, original)
        return original

    tmp.rename(destination)
    try:
        slot = retirement_slot(retired_dir, original.name)
        move_media_file(original, slot)
    except OSError as exc:
        log.warning("could not retire %s after publishing %s: %s; "
                    "leaving the source in place", original, destination, exc)
        return destination
    retirement_ledger.record_retirement(retired_dir, slot, original)
    _remove_orphaned_appledouble(original)
    return destination
