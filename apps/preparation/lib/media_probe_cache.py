# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Cache successful raw ffprobe JSON beside the external media repository.

The cache stores probe facts only. Callers continue applying current policy to
those facts on every invocation. Metadata snapshots and a quiet interval guard
reuse without hashing whole media files; cache misses or cache errors always
fall back to the caller's existing probe runner.
"""

from __future__ import annotations

import hashlib
import json
import logging
import math
import os
import shutil
import stat
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Callable

from lib.encode_cancellation import EncodeCancelled, bound_cancel_event

log = logging.getLogger("organize-media")

_SCHEMA_VERSION = 1
_ENTRY_TYPE = "ffprobe-raw-json"
_MAX_ENTRY_BYTES = 4 * 1024 * 1024
_MAX_AGE_SECONDS = 7 * 24 * 60 * 60
_QUIET_SECONDS = 2
_IDENTITY_FIELDS = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")


def _reject_json_constant(value: str) -> None:
    """Reject nonstandard NaN and Infinity tokens accepted by json.loads."""
    raise ValueError(f"nonstandard JSON constant: {value}")


def _identity(path: Path) -> dict[str, int] | None:
    """Return regular-file identity metadata, or None when unavailable."""
    try:
        info = path.stat()
    except OSError:
        return None
    if not stat.S_ISREG(info.st_mode):
        return None
    return {
        "st_dev": info.st_dev,
        "st_ino": info.st_ino,
        "st_size": info.st_size,
        "st_mtime_ns": info.st_mtime_ns,
        "st_ctime_ns": info.st_ctime_ns,
    }


def _is_quiet(identity: dict[str, int]) -> bool:
    """Require mtime and ctime to be at least two seconds old."""
    now_ns = time.time_ns()
    return all(now_ns - identity[field] >= _QUIET_SECONDS * 1_000_000_000
               for field in ("st_mtime_ns", "st_ctime_ns"))


def _raise_if_cancelled(path: Path, phase: str) -> None:
    """Preserve run cancellation even when a cached answer avoids the runner."""
    event = bound_cancel_event()
    if event is not None and event.is_set():
        raise EncodeCancelled(f"{phase}: ffprobe for {path}")


def _resolve_command(command: list[str], media_path: Path) -> tuple[list[str], Path, dict[str, int]] | None:
    """Canonicalize the executable and media argument, if both are statable."""
    if len(command) < 2:
        return None
    executable_name = shutil.which(command[0]) or command[0]
    try:
        executable = Path(executable_name).resolve(strict=True)
        canonical_media = media_path.resolve(strict=True)
        command_media = Path(command[-1]).resolve(strict=True)
    except (OSError, RuntimeError):
        return None
    if command_media != canonical_media:
        return None
    executable_identity = _identity(executable)
    if executable_identity is None:
        return None

    canonical_command = [str(executable), *command[1:-1], str(canonical_media)]
    return canonical_command, canonical_media, executable_identity


def _expected_key(command: list[str]) -> str:
    """Identify which nonempty ffprobe fact array this command requests."""
    return "packets" if any("packet=" in argument for argument in command) else "streams"


def _valid_stdout(stdout: str | bytes, expected_key: str) -> bytes | None:
    """Return UTF-8 JSON bytes only when the requested fact array is usable."""
    if isinstance(stdout, bytes):
        raw = stdout
        try:
            decoded = stdout.decode("utf-8")
        except UnicodeDecodeError:
            return None
    elif isinstance(stdout, str):
        decoded = stdout
        raw = stdout.encode("utf-8")
    else:
        return None
    try:
        document = json.loads(decoded, parse_constant=_reject_json_constant)
    except (ValueError, UnicodeError):
        return None
    values = document.get(expected_key) if isinstance(document, dict) else None
    if not isinstance(values, list) or not values or any(not isinstance(item, dict) for item in values):
        return None
    return raw


def _cache_directory() -> Path:
    """Return the repository cache directory and reject a symlinked location."""
    repository = Path(__file__).resolve().parents[2]
    directory = repository / "cache" / "media-probe"
    ancestor = directory
    while not ancestor.exists() and not ancestor.is_symlink():
        ancestor = ancestor.parent
    if ancestor.resolve(strict=True) != ancestor:
        raise OSError(f"probe cache ancestor is a symlink: {ancestor}")
    directory.mkdir(parents=True, exist_ok=True)
    if directory.resolve(strict=True) != directory:
        raise OSError(f"probe cache directory resolves outside repository: {directory}")
    return directory


def _read_entry(entry_path: Path) -> bytes | None:
    """Read a bounded regular cache entry without following a symlink."""
    no_follow = getattr(os, "O_NOFOLLOW", None)
    if no_follow is None:
        raise OSError("platform does not support O_NOFOLLOW for probe cache reads")
    flags = os.O_RDONLY | no_follow
    descriptor = os.open(entry_path, flags)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_size <= 0 or info.st_size > _MAX_ENTRY_BYTES:
            raise ValueError("cache entry is not a bounded regular file")
        chunks: list[bytes] = []
        remaining = _MAX_ENTRY_BYTES + 1
        while remaining:
            chunk = os.read(descriptor, min(65536, remaining))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        raw = b"".join(chunks)
        if len(raw) > _MAX_ENTRY_BYTES:
            raise ValueError("cache entry exceeds size limit")
        return raw
    finally:
        os.close(descriptor)


def _load_entry(entry_path: Path, *, key: str, command: list[str],
                media_identity: dict[str, int], executable_identity: dict[str, int],
                expected_key: str) -> str | None:
    """Validate schema, key, identities, age, digest and ffprobe JSON facts."""
    raw = _read_entry(entry_path)
    if raw is None:
        return None
    entry = json.loads(raw, parse_constant=_reject_json_constant)
    if (not isinstance(entry, dict)
            or type(entry.get("version")) is not int
            or entry.get("version") != _SCHEMA_VERSION
            or entry.get("type") != _ENTRY_TYPE):
        raise ValueError("unsupported cache entry schema")
    cached_command = entry.get("command")
    if (not isinstance(entry.get("key"), str) or entry.get("key") != key
            or not isinstance(cached_command, list)
            or any(not isinstance(argument, str) for argument in cached_command)
            or cached_command != command):
        raise ValueError("cache entry key mismatch")
    for field in ("media_identity", "executable_identity"):
        identity = entry.get(field)
        if (not isinstance(identity, dict)
                or set(identity) != set(_IDENTITY_FIELDS)
                or any(type(identity.get(name)) is not int for name in _IDENTITY_FIELDS)):
            raise ValueError(f"invalid {field}")
    if entry.get("media_identity") != media_identity:
        return None
    if entry.get("executable_identity") != executable_identity:
        return None
    created_at = entry.get("created_at")
    updated_at = entry.get("updated_at")
    def valid_timestamp(value: object) -> bool:
        try:
            return (isinstance(value, (int, float)) and not isinstance(value, bool)
                    and math.isfinite(value))
        except OverflowError:
            return False

    if (not valid_timestamp(created_at) or not valid_timestamp(updated_at)
            or updated_at < created_at or updated_at > time.time()):
        raise ValueError("invalid cache creation time")
    age = time.time() - created_at
    if age < 0 or age > _MAX_AGE_SECONDS:
        return None
    stdout = entry.get("stdout")
    if not isinstance(stdout, str):
        raise ValueError("invalid cached stdout")
    raw_stdout = stdout.encode("utf-8")
    digest = entry.get("stdout_sha256")
    if not isinstance(digest, str) or hashlib.sha256(raw_stdout).hexdigest() != digest:
        raise ValueError("cached stdout integrity check failed")
    if _valid_stdout(stdout, expected_key) is None:
        raise ValueError("cached ffprobe JSON facts are invalid")
    return stdout


def _publish_entry(entry_path: Path, *, key: str, command: list[str],
                   media_identity: dict[str, int], executable_identity: dict[str, int],
                   stdout: str) -> None:
    """Atomically publish one fsynced cache record; never touch media files."""
    encoded_stdout = stdout.encode("utf-8")
    created_at = time.time()
    entry = {
        "version": _SCHEMA_VERSION,
        "type": _ENTRY_TYPE,
        "created_at": created_at,
        "updated_at": created_at,
        "key": key,
        "command": command,
        "media_identity": media_identity,
        "executable_identity": executable_identity,
        "stdout": stdout,
        "stdout_sha256": hashlib.sha256(encoded_stdout).hexdigest(),
    }
    raw = json.dumps(entry, separators=(",", ":"), sort_keys=True).encode("utf-8")
    if len(raw) > _MAX_ENTRY_BYTES:
        raise ValueError("probe JSON exceeds cache entry size limit")
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=".probe-", suffix=".tmp", dir=entry_path.parent)
    try:
        os.fchmod(descriptor, 0o600)
        temporary = os.fdopen(descriptor, "wb")
        descriptor = -1
        with temporary:
            temporary.write(raw)
            temporary.flush()
            os.fsync(temporary.fileno())
        # allow-bare-rename: atomically publishes this ffprobe JSON cache entry, not media.
        os.replace(temporary_name, entry_path)
    except BaseException as exc:
        if descriptor >= 0:
            try:
                os.close(descriptor)
            except OSError as cleanup_error:
                log.warning("could not close temporary ffprobe cache descriptor after %s: %s",
                            exc, cleanup_error)
        try:
            os.unlink(temporary_name)
        except OSError as cleanup_error:
            log.warning("could not remove temporary ffprobe cache file %s after %s: %s",
                        temporary_name, exc, cleanup_error)
        raise


def run_cached_probe(command: list[str], *, path: Path, timeout: float,
                     runner: Callable[..., subprocess.CompletedProcess]) -> subprocess.CompletedProcess:
    """Run or reuse a successful ffprobe JSON command without skipping policy.

    Cache hits are allowed only for quiet regular media files with unchanged
    metadata before and after entry access. Failed, incomplete, noisy, stale,
    oversized or malformed probes are never cached. Cache faults warn and use
    the provided cancellable runner, whose exceptions propagate unchanged.
    """
    original_command = list(command)
    _raise_if_cancelled(path, "before starting")
    expected_key = _expected_key(original_command)

    def run_real_probe() -> subprocess.CompletedProcess:
        log.debug("running real ffprobe for %s (%s)", path, expected_key)
        return runner(original_command, timeout=timeout)

    cache_context = None
    try:
        resolved = _resolve_command(original_command, path)
        if resolved is not None:
            canonical_command, canonical_path, executable_identity = resolved
            media_identity = _identity(canonical_path)
            if media_identity is not None:
                key_material = json.dumps(
                    {"version": _SCHEMA_VERSION, "path": str(canonical_path),
                     "command": canonical_command},
                    separators=(",", ":"), sort_keys=True,
                ).encode("utf-8")
                key = hashlib.sha256(key_material).hexdigest()
                directory = _cache_directory()
                cache_context = (
                    canonical_command, canonical_path, executable_identity,
                    media_identity, key, expected_key,
                    directory / f"{key}.json",
                )
    except Exception as exc:
        log.warning("ffprobe cache unavailable for %s: %s", path.name, exc)
    if cache_context is None:
        return run_real_probe()
    (canonical_command, canonical_path, executable_identity, media_identity,
     key, expected_key, entry_path) = cache_context

    before_read = _identity(canonical_path)
    executable_before_read = _identity(Path(canonical_command[0]))
    if before_read == media_identity and _is_quiet(before_read):
        try:
            cached_stdout = _load_entry(
                entry_path, key=key, command=canonical_command,
                media_identity=media_identity,
                executable_identity=executable_identity,
                expected_key=expected_key,
            )
        except FileNotFoundError:
            cached_stdout = None
        except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
            log.warning("could not read ffprobe cache for %s: %s", path.name, exc)
            cached_stdout = None
        after_read = _identity(canonical_path)
        executable_after_read = _identity(Path(canonical_command[0]))
        if (cached_stdout is not None and after_read == before_read
                and _is_quiet(after_read)
                and executable_before_read == executable_identity
                and executable_after_read == executable_before_read):
            _raise_if_cancelled(path, "before reusing cached result")
            log.debug("ffprobe cache hit for %s (%s)", path, expected_key)
            return subprocess.CompletedProcess(original_command, 0, cached_stdout, "")

    result_identity = _identity(canonical_path)
    can_cache = result_identity == media_identity and _is_quiet(result_identity)
    # Keep runner exceptions, especially EncodeCancelled, completely unchanged.
    result = run_real_probe()
    after_probe = _identity(canonical_path)
    current_executable_identity = _identity(Path(canonical_command[0]))
    if (can_cache and after_probe == result_identity and _is_quiet(after_probe)
            and current_executable_identity == executable_identity
            and result.returncode == 0 and result.stdout and not result.stderr):
        raw_stdout = _valid_stdout(result.stdout, expected_key)
        if raw_stdout is not None:
            try:
                stdout = raw_stdout.decode("utf-8")
                _publish_entry(
                    entry_path, key=key, command=canonical_command,
                    media_identity=result_identity,
                    executable_identity=executable_identity, stdout=stdout,
                )
            except (OSError, ValueError, UnicodeError) as exc:
                log.warning("could not write ffprobe cache for %s: %s", path.name, exc)
    return result
