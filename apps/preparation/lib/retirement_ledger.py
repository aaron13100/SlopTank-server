# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Record when a retired original was actually retired, out of band from the filesystem.

tools/lib/retired_original_expiry.py's 14-day undo-window sweep decides a
retired file's age from st_ctime. That works until the retirement directory
itself is moved: a cross-device move resets st_ctime to the move time, not
the original retirement time. t_260909_133547_546 hit this for real on
2026-09-09, when the retirement directory moved from Black to Orange and
every one of the 458 retained files' apparent age reset to zero.

Nothing else on disk can answer "when was this file retired" after that
happens once: mtime belongs to the original media (unrelated to retirement),
and Jellyfin's own DateModified is just a live-scan copy of mtime with no
history. The retirement time has to be recorded when the retirement happens,
beside the retired copy, or it is gone for good.

This module is that record. One JSONL line per retirement, appended (never
rewritten) to ``<retired_dir>/.retirement-ledger.jsonl``, flushed to disk
immediately so a crash loses at most the record in flight, never an earlier
one. A ledger write is deliberately best-effort: record_retirement() never
raises, because losing one ledger line is recoverable (retired_original_expiry
falls back to treating an unrecorded file as needing another signal) while
failing the media replacement it would otherwise have blocked is not.

Reader contract for read_ledger(): the LAST record for a given slot_name
wins, unknown fields are tolerated, and new fields may only ever be added
(never removed or repurposed) without bumping "version".
"""

from __future__ import annotations

import json
import logging
import os
import time
from pathlib import Path

log = logging.getLogger(__name__)

#: The ledger's filename, beside the retired copies it describes.
LEDGER_FILENAME = ".retirement-ledger.jsonl"

#: Bumped only if a field is removed or repurposed; adding a field never
#: requires a bump, so old readers keep working against new records.
FORMAT_VERSION = 1


def ledger_path(retired_dir: Path) -> Path:
    """The ledger file for a given retirement directory.

    :param retired_dir: A retirement directory (holds the retired copies).
    :returns: ``<retired_dir>/.retirement-ledger.jsonl``, which may not exist.
    """
    return retired_dir / LEDGER_FILENAME


def _append_record(path: Path, record: dict[str, object]) -> None:
    """Durably append one JSON line to the ledger file at ``path``.

    Isolated from record_retirement() so a test can replace exactly this
    seam to prove a ledger write failure cannot fail the replacement it
    records, without patching filesystem primitives globally.

    :param path: The ledger file to append to; its parent is created if
        missing.
    :param record: The record to serialize, one JSON object per line.
    :raises OSError: The directory could not be created, or the write,
        flush, or fsync failed. Left to the caller to catch.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, sort_keys=True) + "\n")
        handle.flush()
        os.fsync(handle.fileno())


def record_retirement(retired_dir: Path, slot: Path, source_path: Path | None, *,
                      retired_at: float | None = None,
                      retired_at_source: str = "observed") -> None:
    """Append one retirement record for ``slot`` to ``retired_dir``'s ledger.

    Never raises. Losing a ledger line is recoverable; failing the media
    replacement this records is not, so any failure (creating the ledger
    directory, writing, or even stat-ing ``slot`` for its size) is logged as
    a warning with the underlying error and otherwise swallowed.

    :param retired_dir: The retirement directory the ledger lives beside.
    :param slot: The retired copy, already durably in its slot. Its basename
        becomes ``slot_name`` and its current size becomes ``size_bytes``.
    :param source_path: The absolute path of the library file that was
        replaced, or None when it cannot be recovered (a backfilled record
        for a retirement nothing logged at the time); recorded as an empty
        string in that case.
    :param retired_at: The retirement time as a Unix float. Defaults to the
        current time, for a record written at retirement time; a backfill
        passes an explicit inferred value instead.
    :param retired_at_source: "observed" for a record written at retirement
        time, or "migration-log"/"ctime-floor" for a backfilled inference.
        Lets a later reader tell a real measurement from one.
    """
    when = time.time() if retired_at is None else retired_at
    try:
        size_bytes = slot.stat().st_size
        record = {
            "kind": "retirement",
            "version": FORMAT_VERSION,
            "slot_name": slot.name,
            "source_path": str(source_path) if source_path is not None else "",
            "retired_at": when,
            "size_bytes": size_bytes,
            "retired_at_source": retired_at_source,
        }
        _append_record(ledger_path(retired_dir), record)
    except OSError as exc:
        log.warning(
            "could not append a retirement ledger record for %s in %s: %s "
            "(errno %s); the replacement is unaffected, but this file's undo "
            "window can no longer be dated precisely",
            slot.name, retired_dir, exc.strerror or exc, exc.errno)


def read_ledger(retired_dir: Path) -> dict[str, dict[str, object]]:
    """Read a retirement ledger, keeping only the last record per slot_name.

    A missing ledger file reads as empty rather than raising: most of this
    project's history predates the ledger, and "no ledger yet" is not an
    error. A line that fails to parse, or parses but carries no usable
    slot_name, is skipped with a warning rather than aborting the whole
    read: one damaged line must not hide every other record in the file.

    :param retired_dir: The retirement directory the ledger lives beside.
    :returns: Mapping of slot_name to its last (most recent) record.
    """
    path = ledger_path(retired_dir)
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        return {}
    except OSError as exc:
        log.warning("could not read the retirement ledger %s: %s (errno %s)",
                   path, exc.strerror or exc, exc.errno)
        return {}
    last_by_slot: dict[str, dict[str, object]] = {}
    for line_number, line in enumerate(text.splitlines(), start=1):
        line = line.strip()
        if not line:
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError as exc:
            log.warning("skipping malformed retirement ledger line %s:%d: %s",
                       path, line_number, exc)
            continue
        if not isinstance(record, dict):
            log.warning("skipping non-object retirement ledger line %s:%d",
                       path, line_number)
            continue
        slot_name = record.get("slot_name")
        if not isinstance(slot_name, str) or not slot_name:
            log.warning(
                "skipping retirement ledger line %s:%d with no usable slot_name",
                path, line_number)
            continue
        last_by_slot[slot_name] = record
    return last_by_slot
