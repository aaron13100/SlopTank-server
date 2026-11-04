# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Choosing whether a new encode starts under the macOS background clamp.

THE ASYMMETRY THAT SHAPES THIS MODULE
--------------------------------------
The obvious design, "start clamped and lift the clamp once idle", does not
work. VideoToolbox negotiates its session under the QoS in force when the
encode starts, so a later lift does not restore speed and actively makes it
worse. Measured over the same 30 s of the same file:

    never clamped                 65 fps    11 s
    clamped throughout            17 fps    46 s
    clamped, then lifted at 2 s  5.3 fps   137 s

So SPEED must be chosen before exec, while PROTECTION can still be restored
afterwards (see encode_clamp.EncodeClampGuard, which watches a running
encode and clamps it the moment anyone needs the machine -- deliberately
ONE-WAY, it never lifts).

FAIL-SAFE INVARIANT
--------------------
Every uncertain path (unreachable server, unparseable response, raised
exception) resolves to CLAMPED, which is the old unconditional behaviour. A
broken check here can only cost throughput, never playback quality.
"""

from __future__ import annotations

import logging
import os

from lib.encode_attendance import (
    PlaybackCheck,
    _attendance_reason,
    jellyfin_playback_check,
    resolve_tool,
    unattended_check,
)
from lib.encode_clamp_registry import any_encode_clamped
from lib.organize_media_config import (
    ADAPTIVE_ENCODE_THROTTLE,
    ADAPTIVE_THROTTLE_MIN_IDLE_SECONDS,
    TRANSCODE_NICE,
)

#: Shared with encode_priority.py and transcode.py so encode-supervision
#: lines land together whichever module called in.
log = logging.getLogger("organize-media")


def _priority_prefix(attended_check: PlaybackCheck | None = None, *,
                     clamped: bool | None = None) -> list[str]:
    """Command prefix that makes the encode yield to all foreground work.

    The transcode must never interfere with playback on this slow host. ``nice``
    alone is insufficient: it lowers CPU scheduling priority but does NOTHING for
    disk I/O, and the encode's continuous multi-GB reads/writes on the same volume
    Jellyfin streams from are what starve direct-play range requests (intermittent
    playback freezes).

    On macOS, ``taskpolicy -b`` puts the process in the background QoS clamp, which
    additionally drops its disk I/O to the THROTTLE tier so streaming reads always
    win. The QoS/I/O policy is inherited by ffmpeg's children (and propagates over
    XPC to the VideoToolbox encode service). ``nice`` is still layered on for CPU on
    platforms without ``taskpolicy``. Returns the prefix list (possibly empty if
    neither tool is present).

    The clamp is decided HERE, before exec, and cannot usefully be revisited
    later: VideoToolbox negotiates its session under the QoS in force at start,
    so an encode launched clamped stays slow even if the host goes quiet (and
    lifting it mid-run is measurably worse than leaving it on). When nothing at
    all wants the machine, the ``-b`` prefix is omitted, but encode_priority's
    launch helper actively releases any ambient background tier before exec;
    omission alone is not evidence of an unclamped process. The guard
    re-applies the clamp if someone shows up mid-encode, which does work.

    Returns:
        list[str]: argv prefix to prepend before the ffmpeg binary.
    """
    prefix: list[str] = []
    # Resolved absolutely: cron's PATH lacks /usr/sbin, and a taskpolicy that
    # cannot be found would drop playback protection without saying so.
    taskpolicy_bin = resolve_tool("taskpolicy")
    if clamped is None:
        clamped = _clamp_at_launch(attended_check)
    if os.path.exists(taskpolicy_bin) and clamped:
        prefix += [taskpolicy_bin, "-b"]
    nice_bin = resolve_tool("nice")
    if os.path.exists(nice_bin):
        prefix += [nice_bin, "-n", str(TRANSCODE_NICE)]
    return prefix


def _clamp_at_launch(attended_check: PlaybackCheck | None = None) -> bool:
    """Whether a new encode must start under the background clamp.

    True whenever anyone might want the host, and on every uncertain path, so
    the unclamped fast path is taken only on a positively confirmed idle host.

    An idle host is necessary but not sufficient. A clamped encode sits in the
    disk-I/O THROTTLE tier, which yields to every normal-priority reader, so
    starting a new encode unclamped alongside one does not just make the new
    one faster -- it takes the clamped one to zero. A VideoToolbox encode can
    never climb back out (see :func:`encode_watchdog._uses_hardware_session`),
    so it would stay at zero until the stall watchdog killed it. That is
    exactly how six hours of "Moon (2009)" were destroyed on 2026-09-06 at
    ~90% done.
    """
    if not ADAPTIVE_ENCODE_THROTTLE:
        return True
    if any_encode_clamped():
        log.info("an encode is already clamped; starting this one clamped too "
                 "rather than starving it")
        return True
    check = attended_check or _host_attended_check()
    attended = check()
    if attended is None or attended:
        log.info("host requires the background clamp at launch (reason=%s)",
                 _attendance_reason(check))
        return True
    log.info("host is unattended; starting encode without the background clamp")
    return False


def _host_attended_check():
    """Build the composite "is anyone using this host" probe used at launch and during."""
    return unattended_check(jellyfin_playback_check(log),
                            min_idle_seconds=ADAPTIVE_THROTTLE_MIN_IDLE_SECONDS)
