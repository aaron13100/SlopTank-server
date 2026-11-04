# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Pure parsing of an already-built ffmpeg argv for phase-recovery facts.

No subprocess, no filesystem I/O beyond the injected duration probe: this is
string/path parsing only, independent of encode_phase_observer.py's process
and descriptor probing. Its facts (hardware codec use, target bitrate,
predicted output size) are read by encode_phase_observer.RealPhaseObserver,
which owns validating a command as a real hardware session before trusting
any of them.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Callable


def _explicit_hardware_video_codec(command: tuple[str, ...]) -> bool:
    for index, token in enumerate(command[:-1]):
        option = str(token).lower()
        if option == "-vcodec" or re.fullmatch(r"-(?:c|codec):v(?::\d+)?", option):
            if str(command[index + 1]).lower().endswith("_videotoolbox"):
                return True
    return False


_BITRATE_TOKEN = re.compile(r"(\d+(?:\.\d+)?)([Mk]?)", re.IGNORECASE)
_BITRATE_UNITS = {"": 1, "K": 1_000, "M": 1_000_000}


def _parse_bitrate_bps(token: str) -> int | None:
    match = _BITRATE_TOKEN.fullmatch(token.strip())
    if match is None:
        return None
    return int(float(match.group(1)) * _BITRATE_UNITS[match.group(2).upper()])


def _find_option_value(command: tuple[str, ...], option: str) -> str | None:
    for index, token in enumerate(command[:-1]):
        if str(token) == option:
            return str(command[index + 1])
    return None


def _estimate_expected_output_bytes(
    command: tuple[str, ...], *, duration_probe: Callable[[Path], float | None],
) -> int | None:
    """Video-bitrate x source-duration estimate of the finished output size.

    Deliberately video-only: audio and container overhead are a few percent
    of a multi-Mbps hardware re-encode, and the restart decision this feeds
    already carries its own safety margin (see
    encode_phase_recovery.RESTART_SAFETY_MARGIN_SECONDS). ``-b:v`` is a fixed
    target set by transcode_video_policy._video_encode_args for every
    hardware command, so this is exact for the one thing it estimates
    (encoder output rate x runtime), not a guess.

    Returns None -- never raises -- when the command or source duration
    cannot be read, so a caller that cannot compute this falls back to
    "restart comparison unavailable" rather than a wrong number.
    """
    bitrate_token = _find_option_value(command, "-b:v")
    input_token = _find_option_value(command, "-i")
    if bitrate_token is None or input_token is None:
        return None
    video_bps = _parse_bitrate_bps(bitrate_token)
    if video_bps is None or video_bps <= 0:
        return None
    try:
        duration_seconds = duration_probe(Path(input_token))
    except OSError:
        return None
    if duration_seconds is None or duration_seconds <= 0:
        return None
    return int(duration_seconds * video_bps / 8)
