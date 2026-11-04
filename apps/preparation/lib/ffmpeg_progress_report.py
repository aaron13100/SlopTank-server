# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Progress for an ffmpeg that writes no output file, read from its own report.

EncodeStallWatchdog judges "stuck" by an output file that stops changing. A
whole-file read into the null muxer (media_probe's volumedetect) has no output
file, so before t_260921_182721_855 a flat wall-clock bound was its only
supervision: nothing could tell a starved-but-moving probe from a wedged one.

ffmpeg's global ``-progress <url>`` option appends a key=value block every
stats period. The report grows on schedule even while ffmpeg is stuck, so its
SIZE is not a progress signal; the media position inside it is. The stamp
below is ``(out_time_us, total_size)`` from the last block, which only changes
when ffmpeg actually emits more media.

Only meaningful for DENSE output (an audio stream decoded end to end). Sparse
output, such as a subtitle track extracted from a multi-GB video, can go many
minutes of real reading without emitting a packet, so its media position is
not a sound liveness signal and callers of that shape must not use this.
"""

from __future__ import annotations

from pathlib import Path
from typing import Callable

#: Bytes read from the end of the report per poll: several blocks' worth, so
#: the last complete block is always inside it however long the run has been.
REPORT_TAIL_BYTES = 4096


def with_progress_report(argv: list[str], report: Path) -> list[str]:
    """Return ``argv`` with ``-progress <report>`` placed right after the binary.

    ``-progress`` is a global option, valid before any input. ``argv[0]`` must
    be the ffmpeg binary itself (run_encode adds its priority prefix later).
    """
    return [argv[0], "-progress", str(report), *argv[1:]]


def _last_int(text: str, key: str) -> int:
    """Value of the last ``key=<int>`` line in ``text``, or -1 when absent/N/A."""
    marker = f"{key}="
    for line in reversed(text.splitlines()):
        if line.startswith(marker):
            try:
                return int(line[len(marker):].strip())
            except ValueError:
                return -1
    return -1


def progress_stamp_reader(report: Path) -> Callable[[], tuple[int, int]]:
    """Build an EncodeStallWatchdog ``stamp`` callable for one report file.

    :param report: The path passed to ``with_progress_report``. It lives on
        local temp storage, never on the media volume, so reading it cannot
        block on the volume the encode itself may be wedged on.
    :returns: A callable returning ``(out_time_us, total_size)`` from the
        report's last block, or ``(-1, -1)`` while the report does not exist
        yet. An unreadable report raises OSError, which the watchdog's poll
        loop logs and retries.
    """
    def stamp() -> tuple[int, int]:
        try:
            with open(report, "rb") as handle:
                handle.seek(0, 2)
                size = handle.tell()
                handle.seek(max(size - REPORT_TAIL_BYTES, 0))
                text = handle.read().decode("utf-8", "replace")
        except FileNotFoundError:
            return (-1, -1)
        return (_last_int(text, "out_time_us"), _last_int(text, "total_size"))

    return stamp
