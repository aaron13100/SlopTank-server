#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Run one server-claimed preparation job through the private stdio protocol."""

from __future__ import annotations


def main() -> int:
    """Serve exactly one parent-supplied job and then exit."""
    import sys
    from pathlib import Path

    tools_dir = Path(__file__).resolve().parent
    sys.path.insert(0, str(tools_dir))
    from lib.playback_preparation_ipc import run_stdio_worker

    return run_stdio_worker()


if __name__ == "__main__":
    raise SystemExit(main())
