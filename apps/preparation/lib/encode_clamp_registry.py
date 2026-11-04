# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Process-wide ledger of which encodes are currently clamped.

Read at launch (any_encode_clamped) so a new encode yields to one already
running clamped instead of taking its disk I/O to zero, and read before a
release (stuck_encode_clamped) so a releasable encode never speeds up past a
sibling that can never follow. EncodeClampGuard (encode_clamp.py) is the only
writer; this module holds no opinion about any one encode's own state.
"""

from __future__ import annotations

import threading
from typing import Callable, NamedTuple


class _ClampedEncode(NamedTuple):
    """One clamped encode, as seen by the encodes deciding whether to yield."""

    is_running: Callable[[], bool]
    releasable: bool


_CLAMPED_ENCODES: dict[int, _ClampedEncode] = {}
_CLAMPED_LOCK = threading.Lock()


def _register_clamped(pid: int, is_running: Callable[[], bool] | None = None,
                      *, releasable: bool = False) -> None:
    """Publish that this encode is clamped, so later launches can yield to it.

    :param pid: The clamped process.
    :param is_running: Liveness probe for that process. Stored rather than
        checked now, so a registration can never outlive its encode even if
        :meth:`EncodeClampGuard.stop` is never reached.
    :param releasable: Whether this encode can get its speed back if the clamp
        is lifted. A False here is what makes another encode's release unsafe.
    """
    with _CLAMPED_LOCK:
        _CLAMPED_ENCODES[pid] = _ClampedEncode(is_running or (lambda: True),
                                               releasable)


def _forget_clamped(pid: int) -> None:
    """Drop a pid that is no longer clamped, whether released or finished."""
    with _CLAMPED_LOCK:
        _CLAMPED_ENCODES.pop(pid, None)


def _forget_all_clamped() -> None:
    """Empty the registry. For tests, which must not inherit each other's state."""
    with _CLAMPED_LOCK:
        _CLAMPED_ENCODES.clear()


def _prune_dead_locked() -> None:
    """Drop finished encodes. Caller holds ``_CLAMPED_LOCK``."""
    for pid, entry in list(_CLAMPED_ENCODES.items()):
        try:
            alive = entry.is_running()
        except Exception:  # noqa: BLE001 - an unreadable probe must not
            alive = True   # silently drop a live encode's protection
        if not alive:
            del _CLAMPED_ENCODES[pid]


def any_encode_clamped() -> bool:
    """Whether any encode in this process is currently under the clamp.

    Read at launch time: an encode that starts unclamped beside one of these
    does not merely run faster, it takes the clamped encode's disk I/O to zero.

    Entries whose process has exited are dropped here rather than trusted,
    because the cost of a stale one is silent and unbounded: it would clamp
    every subsequent encode for the life of the process, with nothing in any
    log saying why they all got slow.
    """
    with _CLAMPED_LOCK:
        _prune_dead_locked()
        return bool(_CLAMPED_ENCODES)


def stuck_encode_clamped(exclude_pid: int) -> bool:
    """Whether some OTHER clamped encode could not survive losing the disk.

    Asked before lifting a clamp. A releasable sibling is fine: it comes off
    the clamp on its own next poll and the two stay level. A non-releasable one
    never can, so speeding up past it takes it to zero for good.
    """
    with _CLAMPED_LOCK:
        _prune_dead_locked()
        return any(pid != exclude_pid and not entry.releasable
                   for pid, entry in _CLAMPED_ENCODES.items())
