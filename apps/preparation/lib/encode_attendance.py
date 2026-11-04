# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Fail-closed host-attendance probes used by encode supervision."""

from __future__ import annotations

import logging
import os
import shutil
import subprocess
from typing import Callable, Optional, Sequence


PlaybackCheck = Callable[[], Optional[bool]]

ATTENDANCE_PLAYBACK = "active-jellyfin-playback"
ATTENDANCE_HID = "recent-hid-activity"
ATTENDANCE_UNKNOWN = "unknown-or-probe-failure"
ATTENDANCE_UNATTENDED = "confirmed-unattended"
_ATTENDANCE_REASONS = {
    ATTENDANCE_PLAYBACK,
    ATTENDANCE_HID,
    ATTENDANCE_UNKNOWN,
    ATTENDANCE_UNATTENDED,
}

_TOOL_FALLBACKS = {
    "ioreg": ("/usr/sbin/ioreg",),
    "taskpolicy": ("/usr/sbin/taskpolicy",),
    "nice": ("/usr/bin/nice",),
    "ps": ("/bin/ps",),
}

DEFAULT_MIN_IDLE_SECONDS = 300


def _attendance_reason(check: PlaybackCheck) -> str:
    """The last stable reason published by a composite attendance probe."""
    reason = getattr(check, "last_reason", ATTENDANCE_UNKNOWN)
    return (reason if isinstance(reason, str) and reason in _ATTENDANCE_REASONS
            else ATTENDANCE_UNKNOWN)


def resolve_tool(name: str) -> str:
    """Absolute path to a system tool, without trusting the ambient PATH.

    Prefers PATH (so an override still wins), then the known absolute location,
    then the bare name so a failure surfaces as a nameable OS error rather than
    as silently-missing behaviour.

    :param name: Tool name, e.g. "taskpolicy".
    :returns: A path to run, absolute whenever the tool could be located.
    """
    found = shutil.which(name)
    if found:
        return found
    for candidate in _TOOL_FALLBACKS.get(name, ()):  # noqa: SIM110 - explicit is clearer
        if os.path.exists(candidate):
            return candidate
    return name


def _default_runner(argv: Sequence[str]) -> bool:
    """Run a taskpolicy argv, returning whether it succeeded.

    :param argv: Full argv, e.g. ``["/usr/sbin/taskpolicy", "-b", "-p", "123"]``.
    :returns: True on exit status 0.
    """
    try:
        result = subprocess.run(list(argv), capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError) as exc:
        logging.getLogger("encode_priority").warning(
            "taskpolicy %s failed to launch: %s", " ".join(argv[1:]), exc
        )
        return False
    if result.returncode != 0:
        logging.getLogger("encode_priority").warning(
            "taskpolicy %s exited %d: %s",
            " ".join(argv[1:]), result.returncode, (result.stderr or "").strip()
        )
        return False
    return True


def jellyfin_playback_check(log: logging.Logger | None = None) -> PlaybackCheck:
    """Build a PlaybackCheck backed by Jellyfin's /Sessions endpoint.

    Kept separate from the supervisor so the supervisor can be tested without a
    server, and so the "unknown means watched" policy lives in exactly one place.

    :param log: Logger for transient probe failures.
    :returns: Callable returning True/False/None as per PlaybackCheck.
    """
    logger = log or logging.getLogger("encode_priority")

    def check() -> Optional[bool]:
        try:
            from lib import jellyfin_client
        except ImportError as exc:
            logger.warning("no jellyfin_client, assuming someone is watching: %s", exc)
            return None
        try:
            env = jellyfin_client.load_env()
            token = env.get("JELLYFIN_TOKEN", "")
            base = jellyfin_client.resolve_base_url(env, token) if token else None
            if not base:
                logger.warning("no reachable Jellyfin; assuming someone is watching")
                return None
            playing = jellyfin_client.active_playback_sessions(base, token)
        except Exception as exc:  # noqa: BLE001 - probe must never kill the encode
            logger.warning("playback probe failed (%s: %s); assuming watched",
                           type(exc).__name__, exc)
            return None
        if playing is None:
            return None
        return bool(playing)

    return check


def host_idle_seconds() -> Optional[float]:
    """Seconds since the last keyboard or mouse input on the console.

    Reads IOHIDSystem's HIDIdleTime (nanoseconds). Returns None when the value
    cannot be read, which callers must treat as "someone may be here" rather
    than as idle.

    :returns: Idle seconds, or None if unknown.
    """
    try:
        result = subprocess.run([resolve_tool("ioreg"), "-c", "IOHIDSystem"],
                                capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.SubprocessError) as exc:
        logging.getLogger("encode_priority").warning("cannot read HID idle time: %s", exc)
        return None
    if result.returncode != 0:
        logging.getLogger("encode_priority").warning(
            "ioreg exited %d reading HID idle time", result.returncode)
        return None
    for line in result.stdout.splitlines():
        if "HIDIdleTime" not in line:
            continue
        try:
            return int(line.rsplit("=", 1)[1].strip()) / 1_000_000_000
        except (IndexError, ValueError) as exc:
            logging.getLogger("encode_priority").warning(
                "unparseable HIDIdleTime line %r: %s", line.strip(), exc)
            return None
    logging.getLogger("encode_priority").warning("no HIDIdleTime found in ioreg output")
    return None


def unattended_check(playback_check: PlaybackCheck, *,
                     min_idle_seconds: float = DEFAULT_MIN_IDLE_SECONDS,
                     idle_source: Callable[[], Optional[float]] = host_idle_seconds
                     ) -> PlaybackCheck:
    """Compose playback and console-idle into one "is anyone using this host" check.

    Lifting the clamp speeds the encode up but lets it compete for disk and CPU,
    so it is only safe when nothing else wants the machine. Playback alone is
    not enough: the owner may be working at the keyboard with nothing playing.

    Returns a PlaybackCheck whose True means "attended, keep the clamp". Every
    unknown resolves to True, so an unreadable idle time or an unreachable
    Jellyfin can only cost throughput, never responsiveness.

    :param playback_check: Underlying playback probe.
    :param min_idle_seconds: Console silence required before counting as unattended.
    :param idle_source: Returns console idle seconds, or None if unknown.
    :returns: A PlaybackCheck suitable for :class:`PlaybackAwareThrottle`.
    """

    def check() -> Optional[bool]:
        try:
            watched = playback_check()
        except Exception as exc:  # noqa: BLE001 - probe must never kill the encode
            logging.getLogger("encode_priority").warning(
                "playback check raised (%s: %s); treating host as attended",
                type(exc).__name__, exc)
            setattr(check, "last_reason", ATTENDANCE_UNKNOWN)
            return True
        if watched is None:
            setattr(check, "last_reason", ATTENDANCE_UNKNOWN)
            return True
        if watched:
            setattr(check, "last_reason", ATTENDANCE_PLAYBACK)
            return True
        try:
            idle = idle_source()
        except Exception as exc:  # noqa: BLE001 - probe must never kill the encode
            logging.getLogger("encode_priority").warning(
                "idle probe raised (%s: %s); treating host as attended",
                type(exc).__name__, exc)
            setattr(check, "last_reason", ATTENDANCE_UNKNOWN)
            return True
        if idle is None:
            setattr(check, "last_reason", ATTENDANCE_UNKNOWN)
            return True
        if idle < min_idle_seconds:
            setattr(check, "last_reason", ATTENDANCE_HID)
            return True
        setattr(check, "last_reason", ATTENDANCE_UNATTENDED)
        return False

    setattr(check, "last_reason", ATTENDANCE_UNKNOWN)
    return check
