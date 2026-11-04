#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Media duration via ffprobe, with no dependency on the probe pipeline.

Both tools/lib/media_probe.py and tools/lib/encode_phase_observer.py need
"how long is this file" as a single ffprobe call. The helper used to live in
media_probe, which forced encode_phase_observer (imported by encode_priority,
which media_probe imports for run_encode) to import media_probe back lazily --
a documented circular import. A leaf module both sides import keeps the graph
acyclic: this module imports only the standard library, lib/encode_cancellation
and lib/media_tools, each itself a leaf (media_tools reaches only
lib/organize_media_config, which imports nothing from lib; encode_cancellation
holds the cancellable-capture runner that encode_priority used to own, which is
what had closed the cycle again).
"""

from __future__ import annotations

import json
from pathlib import Path

from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.media_tools import require_tool


def get_media_duration(path: Path) -> float | None:
    """Return media duration in seconds via ffprobe, or None if unavailable.

    :param path: The media file to measure.
    :returns: Duration in seconds, or None when ffprobe fails or reports
        no positive duration.
    :raises MediaToolMissingError: ffprobe cannot be resolved.
    """
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cancellable_capture(
            [ffprobe, "-v", "error", "-show_entries", "format=duration",
             "-of", "json", str(path)],
            # See media_probe._probe_video_audio's timeout comment: a cold
            # read of a large library file can itself take up to a minute or
            # more on this host.
            timeout=300,
        )
        if result.returncode != 0 or not result.stdout:
            return None
        duration = float(json.loads(result.stdout).get("format", {}).get("duration") or 0)
        return duration if duration > 0 else None
    except EncodeCancelled:
        raise
    except Exception:
        # allow-silent-catch: ffprobe failure means duration is unmeasurable;
        # caller already treats None as "duration unknown, skip the check".
        return None
