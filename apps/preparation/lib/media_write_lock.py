#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""One per-target write lock and one unguessable temp for every media writer.

A media writer used to derive its temp name deterministically next to the
target (``.<stem>.<tag><suffix>``) with no per-target mutual exclusion. Two
writers of one target opened the same temp with O_TRUNC; the first renamed
the inode into the library while the second was still writing it, corrupting
the library file. This really happened on 2026-09-25 (three films): each
writer trusted its launcher's lock, but the launchers held different locks
or none (organize-media vs backfill, fix-keyframes-catchup), so nothing
excluded them from each other.

This module makes that shape impossible by construction, in one shared
place: every writer takes a non-blocking ``fcntl.flock`` on the SAME lock
file per target (``.<name>.write.lock``, one name regardless of tag, so
realign, backfill, organize-media, catch-up and keyframes all exclude each
other on the same file) and writes to a ``tempfile.mkstemp`` temp no other
run can open. The temp keeps the ``.<stem>.<tag>.`` shape so existing sweeps
for hidden partials (``.*.transcoding.*`` and kin) still match, and it stays
a dotfile so Jellyfin never indexes it.
"""

from __future__ import annotations

import errno
import fcntl
import os
import stat
import tempfile
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path


class MediaWriteBusy(Exception):
    """Another process holds the target's write lock; carries the target path.

    Raised by `exclusive_media_write` when the lock is already held. Callers
    log it (with the path) and skip that one item; it is never success and
    never stops a batch.
    """

    def __init__(self, message: str, *, path: Path):
        super().__init__(message)
        self.path = path


@contextmanager
def exclusive_media_write(target: Path, tag: str, *,
                          suffix: str | None = None) -> Iterator[Path]:
    """Hold one target's write lock and yield a temp no other run can open.

    Takes a non-blocking ``fcntl.flock`` on
    ``target.with_name(f".{target.name}.write.lock")`` -- ONE lock name for
    every writer regardless of `tag` -- then creates the temp with
    ``tempfile.mkstemp``. On exit removes the temp if it still exists (the
    caller has installed it by then on success) and unlinks the lock file
    while still holding the lock, then unlocks.

    :param target: The library file about to be rewritten. The lock file and
        the temp both live next to it, so both die with the directory and no
        cross-filesystem move is ever needed at install.
    :param tag: The writer's short name (``realign``, ``transcoding``,
        ``subembed``, ...). Kept in the temp name so sweeps for hidden
        partials keep matching; plays NO part in locking, so different tags
        on the same target still exclude each other.
    :param suffix: Temp-file suffix. Defaults to the target's own suffix;
        pass the encode's output suffix when it differs (a transcode turning
        ``.mkv`` into ``.mp4`` must write a ``.mp4`` temp: ffmpeg keys the
        container off the extension, and so does output verification).
    :returns: The temp Path, yielded inside the held lock.
    :raises MediaWriteBusy: Another process holds the target's lock.
    :raises OSError: Any other lock failure (unwritable directory, flock
        unsupported), re-raised as itself with the target in the message.
    """
    lock_path = target.with_name(f".{target.name}.write.lock")
    try:
        fd = os.open(str(lock_path), os.O_CREAT | os.O_RDWR, 0o644)
    except OSError as exc:
        # Opening the lock file is not contention (e.g. the directory went
        # away): report it as itself, never as a busy target.
        raise type(exc)(f"{target}: {exc}") from exc
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as exc:
        os.close(fd)
        if exc.errno in (errno.EACCES, errno.EAGAIN, errno.EWOULDBLOCK):
            raise MediaWriteBusy(
                f"another writer holds it: {target}", path=target) from exc
        raise type(exc)(f"{target}: {exc}") from exc
    tmp_fd, tmp_name = tempfile.mkstemp(
        dir=target.parent, prefix=f".{target.stem}.{tag}.",
        suffix=suffix if suffix is not None else target.suffix)
    os.close(tmp_fd)
    tmp = Path(tmp_name)
    try:
        # mkstemp creates 0600 and ffmpeg -y keeps an existing file's mode,
        # so the installed rewrite would silently tighten the library file.
        if target.exists():
            os.chmod(tmp, stat.S_IMODE(target.stat().st_mode))
        yield tmp
    finally:
        tmp.unlink(missing_ok=True)
        try:
            lock_path.unlink(missing_ok=True)
        finally:
            fcntl.flock(fd, fcntl.LOCK_UN)
            os.close(fd)
