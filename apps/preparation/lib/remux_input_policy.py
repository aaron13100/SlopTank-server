# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Source-container input flags for remuxes that need generated packet timestamps.

AVI files with MPEG-4 B-frames can contain packets without presentation
timestamps. When those packets are copied into Matroska, ffmpeg's muxer rejects
them. `+genpts` asks the demux/input path to generate missing timestamps before
the copy; keep this scoped to AVI-to-MKV remuxes so other media paths retain
their existing input behavior.
"""

from __future__ import annotations

from pathlib import Path


def remux_input_args(source: Path, target_suffix: str) -> list[str]:
    """Return input flags needed for a source/target remux pair."""
    if source.suffix.lower() == ".avi" and target_suffix.lower() == ".mkv":
        return ["-fflags", "+genpts"]
    return []
