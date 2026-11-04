#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Process-wide record of media paths created/replaced during one organize-media run.

Threaded through every pipeline stage (movers, remuxers, transcoders, subtitle
extractors) so main() can report a count, notify Jellyfin, and write
ORGANIZE_MEDIA_UPDATES_FILE at the end of the run without each stage needing
its own return-value path back to main().
"""

from __future__ import annotations

_updated_paths: list[str] = []


def record(path) -> None:
    _updated_paths.append(str(path))


def replace(old_path, new_path) -> None:
    """Swap an already-recorded path for its renamed/re-encoded replacement."""
    try:
        _updated_paths.remove(str(old_path))
    except ValueError:
        # allow-silent-catch: old_path was never recorded (e.g. the pipeline
        # stage runs before any earlier stage renamed this file) -- there is
        # nothing to swap out, so appending new_path below is the only work.
        pass
    _updated_paths.append(str(new_path))


def all_paths() -> list[str]:
    return list(_updated_paths)
