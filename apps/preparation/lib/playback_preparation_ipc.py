# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Versioned stdio boundary for a parent-owned playback-preparation claim.

The parent remains the only caller of the server service. This module passes
one bounded claim and serializes lease/result RPCs over the child's private
stdin/stdout pipes; stdout is never used for business logging.

Version 1 starts with one ``start`` envelope containing ``request`` and
``runtime``. The child emits ``ready``, then correlated ``request`` messages
for ``renew``, ``recheck``, or ``result``. Each receives exactly one matching
``response``; completion or failure is reported as ``complete`` or ``fatal``.
Unknown fields, versions, actions, malformed lines, oversized messages, and
mis-correlated responses fail closed.
"""

from __future__ import annotations

import json
import os
import stat
import sys
import tempfile
import threading
from pathlib import Path
from typing import Any, BinaryIO

from lib.media_preparation_host import MediaPreparationHost, use_preparation_host


PROTOCOL_NAME = "org.sloptank.playback-preparation-stdio"
PROTOCOL_VERSION = 1
MAX_MESSAGE_BYTES = 128 * 1024
MAX_ERROR_TEXT = 2048
_ACTIONS = frozenset({"renew", "recheck", "result", "idle", "replace"})
_RUNTIME_FIELDS = frozenset({
    "jobWorkspace", "profileRoot", "mediaRoot", "ffmpegPath", "ffprobePath",
})
_SECRET_ENV_PREFIXES = ("JELLYFIN_", "SLOPTANK_", "E2E_")
_SECRET_ENV_MARKERS = (
    "TOKEN", "PASSWORD", "PASSWD", "SECRET", "API_KEY", "APIKEY",
    "AUTHORIZATION", "CREDENTIAL",
)


class WorkerProtocolError(RuntimeError):
    """A bounded stdio message or its parent-owned runtime configuration is invalid."""

    def __init__(self, code: str, message: str, cause: BaseException | None = None):
        self.code = code
        self.message = message
        self.cause = cause
        super().__init__(f"{code}: {message}")


class ParentRequestError(WorkerProtocolError):
    """The parent refused an RPC and returned its typed failure details."""


def _reject_constant(value: str) -> None:
    raise ValueError(f"non-finite JSON number is not allowed: {value}")


def _object_without_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON field: {key}")
        result[key] = value
    return result


def _decode_line(stream: BinaryIO, *, stage: str) -> dict[str, Any]:
    try:
        raw = stream.readline(MAX_MESSAGE_BYTES + 1)
    except (OSError, ValueError) as exc:
        raise WorkerProtocolError("ipc-read-failed", f"could not read {stage} message", exc) from exc
    if not raw:
        raise WorkerProtocolError("ipc-unexpected-eof", f"parent closed the pipe while {stage}")
    if len(raw) > MAX_MESSAGE_BYTES:
        raise WorkerProtocolError("ipc-message-too-large", f"{stage} message exceeds the size limit")
    if not raw.endswith(b"\n"):
        raise WorkerProtocolError("ipc-line-incomplete", f"{stage} message is not newline terminated")
    try:
        value = json.loads(
            raw[:-1].decode("utf-8", errors="strict"),
            object_pairs_hook=_object_without_duplicates,
            parse_constant=_reject_constant,
        )
    except (UnicodeDecodeError, ValueError, TypeError) as exc:
        raise WorkerProtocolError("ipc-invalid-json", f"{stage} message is not valid JSON", exc) from exc
    if not isinstance(value, dict):
        raise WorkerProtocolError("ipc-invalid-shape", f"{stage} message must be a JSON object")
    return value


def _encode_line(stream: BinaryIO, value: dict[str, Any], *, stage: str) -> None:
    try:
        encoded = json.dumps(
            value, ensure_ascii=True, allow_nan=False, separators=(",", ":"),
        ).encode("utf-8") + b"\n"
    except (TypeError, ValueError) as exc:
        raise WorkerProtocolError("ipc-encode-failed", f"could not encode {stage} message", exc) from exc
    if len(encoded) > MAX_MESSAGE_BYTES:
        raise WorkerProtocolError("ipc-message-too-large", f"{stage} message exceeds the size limit")
    try:
        stream.write(encoded)
        stream.flush()
    except (OSError, ValueError) as exc:
        raise WorkerProtocolError("ipc-write-failed", f"could not write {stage} message", exc) from exc


def _require_keys(value: dict[str, Any], required: set[str], *, stage: str) -> None:
    if value.keys() != required:
        missing = sorted(required - value.keys())
        unknown = sorted(value.keys() - required)
        raise WorkerProtocolError(
            "ipc-invalid-shape",
            f"{stage} message has missing fields {missing} and unknown fields {unknown}",
        )


def _require_protocol(value: dict[str, Any], *, stage: str) -> None:
    if value.get("protocol") != PROTOCOL_NAME:
        raise WorkerProtocolError("ipc-unknown-protocol", f"{stage} protocol name is unsupported")
    version = value.get("version")
    if type(version) is not int or version != PROTOCOL_VERSION:
        raise WorkerProtocolError("ipc-unknown-version", f"{stage} protocol version is unsupported")


def _absolute_directory(value: Any, name: str) -> Path:
    if not isinstance(value, str) or not value or "\0" in value:
        raise WorkerProtocolError("runtime-path-invalid", f"runtime {name} must be a nonempty path")
    path = Path(value)
    if not path.is_absolute():
        raise WorkerProtocolError("runtime-path-invalid", f"runtime {name} must be absolute")
    try:
        info = path.stat()
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise WorkerProtocolError("runtime-path-unavailable", f"runtime {name} is unavailable", exc) from exc
    if not stat.S_ISDIR(info.st_mode):
        raise WorkerProtocolError("runtime-path-not-directory", f"runtime {name} is not a directory")
    return resolved


def _executable_file(value: Any, name: str) -> Path:
    if not isinstance(value, str) or not value or "\0" in value:
        raise WorkerProtocolError("runtime-path-invalid", f"runtime {name} must be a nonempty path")
    path = Path(value)
    if not path.is_absolute():
        raise WorkerProtocolError("runtime-path-invalid", f"runtime {name} must be absolute")
    try:
        info = path.stat()
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise WorkerProtocolError("runtime-path-unavailable", f"runtime {name} is unavailable", exc) from exc
    if not stat.S_ISREG(info.st_mode) or not os.access(resolved, os.X_OK):
        raise WorkerProtocolError("runtime-executable-invalid", f"runtime {name} is not an executable regular file")
    return resolved


def configure_runtime(runtime: Any) -> dict[str, Path]:
    """Apply the fixed path contract before importing the existing media pipeline."""
    if not isinstance(runtime, dict) or runtime.keys() != _RUNTIME_FIELDS:
        raise WorkerProtocolError(
            "runtime-config-invalid",
            "runtime config must contain exactly jobWorkspace, profileRoot, mediaRoot, ffmpegPath, and ffprobePath",
        )
    configured = {
        "jobWorkspace": _absolute_directory(runtime["jobWorkspace"], "jobWorkspace"),
        "profileRoot": _absolute_directory(runtime["profileRoot"], "profileRoot"),
        "mediaRoot": _absolute_directory(runtime["mediaRoot"], "mediaRoot"),
        "ffmpegPath": _executable_file(runtime["ffmpegPath"], "ffmpegPath"),
        "ffprobePath": _executable_file(runtime["ffprobePath"], "ffprobePath"),
    }
    if configured["jobWorkspace"] == configured["profileRoot"]:
        raise WorkerProtocolError("runtime-path-overlap", "job workspace must be separate from the selected profile")

    # The worker never needs account credentials or an API key. Remove inherited
    # server/client secrets before any existing pipeline module is imported.
    for name in tuple(os.environ):
        upper_name = name.upper()
        if (upper_name.startswith(_SECRET_ENV_PREFIXES)
                or any(marker in upper_name for marker in _SECRET_ENV_MARKERS)):
            os.environ.pop(name, None)

    # These variables are read once by organize_media_config. Credentials are
    # intentionally absent; all service calls use the parent RPC channel.
    os.environ["MEDIASERVER_ROOT"] = str(configured["profileRoot"])
    os.environ["MEDIASERVER_MEDIA_ROOT"] = str(configured["mediaRoot"])
    os.environ["FFMPEG"] = str(configured["ffmpegPath"])
    os.environ["FFPROBE"] = str(configured["ffprobePath"])
    os.environ["TMPDIR"] = str(configured["jobWorkspace"])
    os.environ["TMP"] = str(configured["jobWorkspace"])
    os.environ["TEMP"] = str(configured["jobWorkspace"])
    tempfile.tempdir = str(configured["jobWorkspace"])
    return configured


class StdioRpc(MediaPreparationHost):
    """Serialized, correlated RPC client for one parent-owned claim."""

    def __init__(self, reader: BinaryIO, writer: BinaryIO, request: dict[str, Any]):
        self._reader = reader
        self._writer = writer
        self._lock = threading.Lock()
        self._next_id = 0
        self._request_id = request["requestId"]
        self._item_id = request["itemId"]
        self._bound_request = dict(request)
        self._current_path = Path(request["path"])

    def preflight(self, path: Path) -> str:
        """Confirm the pipeline is preparing the path bound to this claim."""
        if path != self._current_path:
            raise WorkerProtocolError("ipc-preflight-path-mismatch", "preflight path differs from the bound claim")
        return self._item_id

    def replace(
        self, tmp: Path, original: Path, new_path: Path | None, *, item_id: object,
    ) -> Path:
        """Ask the parent to copy staged media into the bound claim."""
        if original != self._current_path:
            raise WorkerProtocolError("ipc-replace-path-mismatch", "replacement source differs from the bound claim")
        if isinstance(item_id, str) and item_id != self._item_id:
            raise WorkerProtocolError("ipc-replace-item-mismatch", "replacement item id differs from the bound claim")
        target = original if new_path is None or new_path == original else new_path
        payload = self._call("replace", {
            "itemId": self._item_id,
            "originalPath": str(original),
            "stagedPath": str(tmp),
            "destinationPath": str(target),
        })
        if payload.keys() != {"path"} or not isinstance(payload["path"], str):
            raise WorkerProtocolError("ipc-invalid-replace-ack", "parent replacement response must contain one path")
        if payload["path"] != str(target):
            raise WorkerProtocolError("ipc-replace-path-mismatch", "parent returned a path different from the requested destination")
        try:
            tmp.unlink(missing_ok=True)
        except OSError as exc:
            raise WorkerProtocolError("ipc-staged-unlink-failed", "could not unlink acknowledged staged media", exc) from exc
        self._current_path = target
        return target

    def sessions_are_idle(self) -> bool:
        """Ask the parent for the authoritative playback idle state."""
        payload = self._call("idle", {})
        if payload.keys() != {"idle"} or type(payload["idle"]) is not bool:
            raise WorkerProtocolError("ipc-invalid-idle-ack", "parent idle response must contain one boolean idle field")
        return payload["idle"]

    def _call(self, action: str, payload: dict[str, Any]) -> dict[str, Any]:
        if action not in _ACTIONS:
            raise WorkerProtocolError("ipc-action-refused", f"unsupported RPC action: {action}")
        with self._lock:
            self._next_id += 1
            call_id = self._next_id
            _encode_line(self._writer, {
                "protocol": PROTOCOL_NAME,
                "version": PROTOCOL_VERSION,
                "type": "request",
                "id": call_id,
                "action": action,
                "payload": payload,
            }, stage="request")
            response = _decode_line(self._reader, stage="response")
            _require_keys(response, {"protocol", "version", "type", "id", "ok", "payload", "error"}, stage="response")
            _require_protocol(response, stage="response")
            if response["type"] != "response" or type(response["id"]) is not int or response["id"] != call_id:
                raise WorkerProtocolError("ipc-response-correlation-failed", "parent response does not match the outstanding request")
            if type(response["ok"]) is not bool:
                raise WorkerProtocolError("ipc-invalid-shape", "parent response field ok must be boolean")
            if response["ok"]:
                if response["error"] is not None or not isinstance(response["payload"], dict):
                    raise WorkerProtocolError("ipc-invalid-shape", "successful parent response must contain an object payload and no error")
                return response["payload"]
            error = response["error"]
            if response["payload"] is not None or not isinstance(error, dict):
                raise WorkerProtocolError("ipc-invalid-shape", "failed parent response must contain an error and no payload")
            _require_keys(error, {"code", "message"}, stage="parent error")
            code, message = error["code"], error["message"]
            if (not isinstance(code, str) or not code or len(code) > 128
                    or not isinstance(message, str) or not message or len(message) > MAX_ERROR_TEXT):
                raise WorkerProtocolError("ipc-invalid-shape", "parent error code/message is invalid")
            raise ParentRequestError(code, message)

    def renew(self, request: dict[str, Any]) -> None:
        self._require_current_request(request, "renew")
        payload = self._call("renew", {"request": request})
        if payload != {"renewed": True}:
            raise WorkerProtocolError("ipc-invalid-renew-ack", "parent did not confirm lease renewal")

    def recheck(self, request: dict[str, Any]) -> dict[str, Any]:
        self._require_current_request(request, "recheck")
        payload = self._call("recheck", {"request": request})
        if payload.keys() != {"request"} or not isinstance(payload["request"], dict):
            raise WorkerProtocolError("ipc-invalid-recheck", "parent recheck response must contain one request")
        rebound = payload["request"]
        from lib.playback_contract import validate_request_shape

        try:
            validate_request_shape(rebound)
        except (TypeError, ValueError) as exc:
            raise WorkerProtocolError("ipc-invalid-recheck", "parent returned an invalid rebound claim", exc) from exc
        if rebound.get("requestId") != self._request_id or rebound.get("itemId") != self._item_id:
            raise WorkerProtocolError("ipc-recheck-binding-mismatch", "parent recheck changed request or item identity")
        self._bound_request = dict(rebound)
        self._current_path = Path(rebound["path"])
        return rebound

    def submit_result(self, result: dict[str, Any]) -> None:
        from lib.playback_contract import validate_result

        try:
            validate_result(self._bound_request, result)
        except (TypeError, ValueError) as exc:
            raise WorkerProtocolError("ipc-result-binding-invalid", "worker result does not match the current server claim", exc) from exc
        payload = self._call("result", {"result": result})
        if payload != {"accepted": True}:
            raise WorkerProtocolError("ipc-invalid-result-ack", "parent did not confirm result submission")

    def _require_current_request(self, request: dict[str, Any], action: str) -> None:
        if (not isinstance(request, dict)
                or request.get("requestId") != self._request_id
                or request.get("itemId") != self._item_id
                or request != self._bound_request):
            raise WorkerProtocolError("ipc-request-binding-mismatch", f"{action} request changed its current binding")


def _read_start(reader: BinaryIO) -> tuple[dict[str, Any], dict[str, Any]]:
    start = _decode_line(reader, stage="start")
    _require_keys(start, {"protocol", "version", "type", "request", "runtime"}, stage="start")
    _require_protocol(start, stage="start")
    if start["type"] != "start" or not isinstance(start["request"], dict):
        raise WorkerProtocolError("ipc-invalid-start", "first message must be a start envelope with a bound request")
    if not isinstance(start["runtime"], dict):
        raise WorkerProtocolError("runtime-config-invalid", "start runtime field must be an object")
    return start["request"], start["runtime"]


def _event(writer: BinaryIO, event_type: str, **fields: Any) -> None:
    _encode_line(writer, {
        "protocol": PROTOCOL_NAME,
        "version": PROTOCOL_VERSION,
        "type": event_type,
        **fields,
    }, stage=event_type)


def run_stdio_worker(
    reader: BinaryIO | None = None,
    writer: BinaryIO | None = None,
    stderr: Any = None,
) -> int:
    """Run one parent-claimed job through the existing worker and planner."""
    reader = reader or sys.stdin.buffer
    writer = writer or sys.stdout.buffer
    stderr = stderr or sys.stderr
    try:
        request, runtime = _read_start(reader)
        configure_runtime(runtime)
        import logging

        logging.basicConfig(stream=stderr, level=logging.INFO)
        from lib.playback_contract import validate_request
        from lib.playback_preparation_worker import run_one_claim

        try:
            bound_request = validate_request(request)
        except (TypeError, ValueError) as exc:
            raise WorkerProtocolError("claim-invalid", "parent supplied an invalid playback-preparation claim", exc) from exc
        rpc = StdioRpc(reader, writer, bound_request)
        _event(writer, "ready", requestId=bound_request["requestId"])
        # Existing planner and pipeline output must not contaminate the JSONL
        # protocol. The RPC writer retains the captured binary protocol stream.
        from contextlib import redirect_stdout

        with redirect_stdout(stderr):
            with use_preparation_host(rpc):
                summary = run_one_claim(
                    claim_request=lambda: bound_request,
                    submit=rpc.submit_result,
                    renew_request=rpc.renew,
                    recheck_request=rpc.recheck,
                )
        if not isinstance(summary, dict) or summary.get("requestId") != bound_request["requestId"]:
            raise WorkerProtocolError("worker-summary-invalid", "existing worker returned an invalid completion summary")
        _event(writer, "complete", requestId=bound_request["requestId"], status=summary.get("status"),
               outcome=summary.get("outcome"), reasonCode=summary.get("reasonCode"), error=None)
        return 0
    except WorkerProtocolError as exc:
        try:
            _event(writer, "fatal", error={"code": exc.code, "message": exc.message[:MAX_ERROR_TEXT]})
        except WorkerProtocolError as write_error:
            print(f"{write_error.code}: {write_error.message}", file=stderr)
        print(f"{exc.code}: {exc.message}", file=stderr)
        if exc.cause is not None:
            print(f"cause: {type(exc.cause).__name__}: {exc.cause}", file=stderr)
        return 2
    except Exception as exc:  # noqa: BLE001 - parent must observe unexpected failures
        code = "worker-unhandled-error"
        message = f"{type(exc).__name__}: {exc}"[:MAX_ERROR_TEXT]
        try:
            _event(writer, "fatal", error={"code": code, "message": message})
        except WorkerProtocolError as write_error:
            print(f"{write_error.code}: {write_error.message}", file=stderr)
        print(f"{code}: {message}", file=stderr)
        return 1
