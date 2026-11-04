# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Bounded macOS process/output observer for phase-aware clamp recovery."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import re
import selectors
import stat
import subprocess
import time
from typing import Callable, Sequence

from lib.encode_command_facts import (
    _estimate_expected_output_bytes,
    _explicit_hardware_video_codec,
)
from lib.encode_phase_recovery import PhaseTelemetry
from lib.encode_phase_supervisor import PhaseRecoverySupervisor
from lib.media_duration import get_media_duration
from lib.organize_media_config import (
    HARDWARE_ENCODE_UNCLAMPED_BYTES_PER_SECOND,
    HARDWARE_RESTART_SAFETY_MARGIN_SECONDS,
)


_PS = Path("/bin/ps")
_LSOF = Path("/usr/sbin/lsof")
_PROBE_TIMEOUT_SECONDS = 2.0
_MAX_PS_BYTES = 1024
_MAX_LSOF_BYTES = 4096
_PS_ROW = re.compile(
    r"^\s*(?P<pid>\d+)\s+"
    r"(?P<started>\w{3}\s+\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4})\s+"
    r"(?P<state>\S+)\s+"
    r"(?P<command>\S(?:.*\S)?)\s*$"
)


@dataclass(frozen=True, slots=True)
class ProcessSnapshot:
    """Pinned process identity plus positive/negative/unknown liveness.

    ``identity`` is pid+start-time only, deliberately stable across a process
    re-exec'ing its OWN image (see ``_default_process_probe``). ``comm`` is
    the current, volatile executable name, compared only within one
    observation's own before/after pair -- never against a snapshot from a
    different call -- so it can catch a real replacement mid-observation
    without also flagging this codebase's own taskpolicy->nice->ffmpeg
    launch-clamp chain, which legitimately changes comm under the same pid.
    """

    identity: str
    running: bool | None
    comm: str = ""


def _collect_probe(
    process: subprocess.Popen, maximum: int,
) -> subprocess.CompletedProcess | None:
    selector = selectors.DefaultSelector()
    streams = {process.stdout: "stdout", process.stderr: "stderr"}
    buffers = {"stdout": bytearray(), "stderr": bytearray()}
    deadline = time.monotonic() + _PROBE_TIMEOUT_SECONDS
    try:
        for stream, name in streams.items():
            if stream is None:
                return None
            selector.register(stream, selectors.EVENT_READ, name)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return None
            events = selector.select(remaining)
            if not events:
                return None
            for key, _mask in events:
                target = buffers[key.data]
                read_size = min(4096, maximum + 1 - len(target))
                chunk = os.read(key.fd, read_size)
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                target.extend(chunk)
                if len(target) > maximum:
                    return None
        returncode = process.wait(timeout=max(0.01, deadline - time.monotonic()))
        return subprocess.CompletedProcess(
            process.args, returncode,
            bytes(buffers["stdout"]), bytes(buffers["stderr"]))
    except (OSError, subprocess.SubprocessError):
        return None
    finally:
        selector.close()
        for stream in streams:
            if stream is not None:
                try:
                    stream.close()
                except OSError:
                    # allow-silent-catch: bounded probe cleanup after its
                    # fail-closed result is already determined
                    pass
        if process.poll() is None:
            try:
                process.kill()
            except OSError:
                # allow-silent-catch: child already exited during probe cleanup
                pass
            try:
                process.wait(timeout=0.5)
            except (OSError, subprocess.SubprocessError):
                # allow-silent-catch: bounded child cleanup cannot change the
                # already fail-closed probe result
                pass


def _run_probe(argv: Sequence[str], *, maximum: int) -> subprocess.CompletedProcess | None:
    if isinstance(maximum, bool) or not isinstance(maximum, int) or maximum <= 0:
        return None
    try:
        process = subprocess.Popen(
            list(argv), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except OSError:
        return None
    return _collect_probe(process, maximum)


def _default_process_probe(pid: int, *, probe_runner=_run_probe) -> ProcessSnapshot:
    result = probe_runner(
        (_PS, "-ww", "-p", str(pid), "-o", "pid=", "-o", "lstart=", "-o", "stat=",
         "-o", "comm="),
        maximum=_MAX_PS_BYTES)
    if result is None:
        return ProcessSnapshot("", None)
    if result.returncode != 0 or not result.stdout.strip():
        return ProcessSnapshot("", False if result.returncode == 1 else None)
    try:
        row = result.stdout.decode("ascii", "strict")
    except UnicodeDecodeError:
        return ProcessSnapshot("", None)
    match = _PS_ROW.fullmatch(row)
    if match is None or int(match.group("pid")) != pid:
        return ProcessSnapshot("", None)
    state = match.group("state")
    # comm is NOT part of the pinned identity: a launch-clamped encode loads
    # taskpolicy, then nice, then ffmpeg in place under this SAME pid (see
    # encode_priority._priority_prefix), so comm legitimately changes within
    # the process's first moments. Baking it into the cross-call identity
    # made every launch-clamped encode fail identity on its very first
    # observation, 100% of the time -- caught live running this against a
    # real launch-clamped hardware encode (t_260906_145134_255). pid+start
    # time is stable across that chain because replacing a process's own
    # program image does not reset its start time.
    identity = hashlib.sha256(
        f"{pid}\0{match.group('started')}".encode("ascii")
    ).hexdigest()
    return ProcessSnapshot(identity, not any(flag in state for flag in "TZ"),
                           comm=match.group("command"))


def _default_descriptor_probe(
    pid: int, path: Path, *, probe_runner=_run_probe,
) -> bool | None:
    try:
        canonical_path = path.resolve(strict=True)
    except OSError:
        return None
    result = probe_runner(
        (_LSOF, "-a", "-p", str(pid), "-Fafn", "--", str(canonical_path)),
        maximum=_MAX_LSOF_BYTES)
    if result is None:
        return None
    if result.returncode == 1 and not result.stdout:
        return False
    if result.returncode != 0:
        return None
    try:
        fields = result.stdout.decode("utf-8", "strict").splitlines()
    except UnicodeDecodeError:
        return None
    return _descriptor_fields_read_path(fields, canonical_path, pid)


def _descriptor_fields_read_path(fields: list[str], path: Path, pid: int) -> bool:
    if [field[1:] for field in fields if field.startswith("p")] != [str(pid)]:
        return False
    descriptors: list[tuple[str, str]] = []
    access = ""
    name = ""
    for field in fields + ["f"]:
        if field.startswith("f"):
            if access and name:
                descriptors.append((access, name))
            access = ""
            name = ""
        elif field.startswith("a"):
            access = field[1:]
        elif field.startswith("n"):
            name = field[1:]
    target = str(path)
    has_read = any(access == "r" and name == target for access, name in descriptors)
    has_write = any(
        access in {"w", "u"} and name == target for access, name in descriptors)
    return has_read and has_write


def _regular_stat(path: Path):
    try:
        info = path.lstat()
    except OSError as error:
        raise OSError(f"output is unavailable: {path.name}") from error
    if not stat.S_ISREG(info.st_mode):
        raise OSError(f"output is not a regular file: {path.name}")
    return info


def _require_same_running_process(
    before: ProcessSnapshot, after: ProcessSnapshot, expected_identity: str,
) -> None:
    """Refuse unless pid+start-time is pinned AND comm held steady just now.

    comm is compared before-vs-after THIS observation only, never against a
    value from a previous call: see ProcessSnapshot's docstring for why a
    cross-call comm comparison is wrong for this codebase's own launch-clamp
    chain. A same-observation comm change (something replaced this pid while
    we were looking at it) is still refused.
    """
    if (
        before.running is not True
        or after.running is not True
        or before.identity != expected_identity
        or after.identity != expected_identity
        or before.comm != after.comm
    ):
        raise OSError("phase observer process identity changed during observation")


class RealPhaseObserver:
    """Pin one launched process and one output inode, then sample both."""

    def __init__(
        self,
        pid: int,
        command: tuple[str, ...],
        progress_path: Path,
        *,
        process_probe: Callable[[int], ProcessSnapshot] = _default_process_probe,
        descriptor_probe: Callable[[int, Path], bool | None] = _default_descriptor_probe,
        duration_probe: Callable[[Path], float | None] | None = None,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        if pid <= 0:
            raise ValueError("phase observer requires a positive pid")
        if not _explicit_hardware_video_codec(command):
            raise ValueError("phase observer requires a hardware encode command")
        self.path = Path(progress_path)
        if not self.path.is_absolute():
            raise ValueError("phase observer output must be absolute")
        if self.path.is_symlink():
            raise OSError("phase observer output is not a regular file")
        initial_process = process_probe(pid)
        if not isinstance(initial_process, ProcessSnapshot):
            raise OSError("process identity probe returned invalid data")
        if initial_process.running is not True or not initial_process.identity:
            raise OSError("process identity is not positively running")
        self.pid = pid
        self.command = command
        self.process_identity = initial_process.identity
        self._process_probe = process_probe
        self._descriptor_probe = descriptor_probe
        self._clock = clock
        self._inode: tuple[int, int] | None = None
        self._previous: tuple[int, int] | None = None
        # One-time, at construction: the source duration cannot change during
        # the encode, so there is no reason to re-probe it on every observe().
        # None (bitrate/input unparseable, or ffprobe failed) just means the
        # restart comparison stays unavailable; it never blocks the encode.
        if duration_probe is None:
            duration_probe = get_media_duration
        self.expected_output_bytes = _estimate_expected_output_bytes(
            command, duration_probe=duration_probe)
        if self.path.exists():
            self._remember(_regular_stat(self.path))

    def _remember(self, info) -> None:
        identity = (info.st_dev, info.st_ino)
        if self._inode is not None and identity != self._inode:
            raise OSError("phase observer output identity changed")
        self._inode = identity
        self._previous = (info.st_size, info.st_mtime_ns)

    def observe(self) -> PhaseTelemetry:
        process_before = self._process_probe(self.pid)
        if not isinstance(process_before, ProcessSnapshot):
            process_before = ProcessSnapshot("", None)
        before = _regular_stat(self.path)
        identity = (before.st_dev, before.st_ino)
        if self._inode is not None and identity != self._inode:
            raise OSError("phase observer output identity changed")
        previous = self._previous
        reads_output = self._descriptor_probe(self.pid, self.path)
        if reads_output not in (True, False, None):
            reads_output = None
        after = _regular_stat(self.path)
        if (after.st_dev, after.st_ino) != identity:
            raise OSError("phase observer output changed during observation")
        process_after = self._process_probe(self.pid)
        if not isinstance(process_after, ProcessSnapshot):
            process_after = ProcessSnapshot("", None)
        _require_same_running_process(
            process_before, process_after, self.process_identity)
        self._inode = identity
        self._previous = (after.st_size, after.st_mtime_ns)
        return PhaseTelemetry(
            pid=self.pid,
            process_identity=self.process_identity,
            observed_at=self._clock(),
            process_running=True,
            command_uses_hardware=True,
            previous_output_size=previous[0] if previous is not None else None,
            output_size=after.st_size,
            previous_output_mtime_ns=previous[1] if previous is not None else None,
            output_mtime_ns=after.st_mtime_ns,
            reads_own_output=reads_output if previous is not None else None,
            attendance_reason=None,
            expected_output_bytes=self.expected_output_bytes,
        )


def default_phase_recovery_factory(
    pid: int, command: tuple[str, ...], progress_path: Path, *,
    restart_enabled: bool = True,
) -> PhaseRecoverySupervisor:
    """Build the fixed real observer used by ordinary hardware encodes."""
    observer = RealPhaseObserver(pid, command, progress_path)
    return PhaseRecoverySupervisor(
        expected_pid=pid,
        expected_process_identity=observer.process_identity,
        observer=observer.observe,
        clock=time.monotonic,
        max_telemetry_age_seconds=_PROBE_TIMEOUT_SECONDS + 1.0,
        unclamped_bytes_per_second=HARDWARE_ENCODE_UNCLAMPED_BYTES_PER_SECOND,
        restart_safety_margin_seconds=HARDWARE_RESTART_SAFETY_MARGIN_SECONDS,
        restart_enabled=restart_enabled,
    )
