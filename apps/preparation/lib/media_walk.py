# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""The one media-library file enumerator: hidden entries never appear.

Every writer in this repo that stages media (transcode, remux, faststart,
audio reorder, subtitle embed, ...) writes a hidden dot-prefixed temp name
such as ``.c298-remux.tmp.mp4`` or ``.<name>.transcoding.mp4``, and macOS adds
``._*`` AppleDouble companions. Those are another job's in-progress output,
never a library file. c178 (2026-10-01): the backfill walker accepted one of
them as a candidate and a single refusal on it cost the whole night.

The exclusion lives here, at the enumeration source, rather than as a
per-call-site name filter that each new walker has to remember.

Limitations: only entries BELOW ``root`` are judged, so a root that itself
lives under a hidden directory still walks. Housekeeping that must SEE hidden
temps (orphan sweeps, junk quarantine) deliberately does not use this.
"""

from __future__ import annotations

import fnmatch
import os
from pathlib import Path
from typing import Iterator


def iter_visible_files(root: Path, pattern: str = "*") -> Iterator[Path]:
    """Yield regular files under ``root`` whose names match ``pattern``.

    :param root: Directory to walk recursively. Symlinked directories are not
        followed (matching ``Path.rglob``).
    :param pattern: Case-sensitive ``fnmatch`` pattern applied to the file
        name, e.g. ``"*.mkv"``.
    :returns: Paths of files only. Hidden files (name starting ``.``) and
        everything inside hidden directories are skipped.
    """
    for directory, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(name for name in dirnames if not name.startswith("."))
        for name in sorted(filenames):
            if name.startswith(".") or not fnmatch.fnmatchcase(name, pattern):
                continue
            path = Path(directory) / name
            if path.is_file():
                yield path
