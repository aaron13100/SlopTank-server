# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Authenticated, bounded transport for the playback-preparation worker.

The server owns the queue and issues one complete contract-v1 request from
``POST /PlaybackPreparation/Claim``.  This module only transports that request
and its exact result; it does not classify media, write media, or persist
readiness state.
"""

from __future__ import annotations

import ipaddress
import json
import os
import selectors
import subprocess
import time
from collections.abc import Mapping
from types import SimpleNamespace
from typing import Any, Callable
from urllib.parse import urlsplit

from lib import jellyfin_client
from lib.playback_contract import (
    canonical_json_bytes,
    validate_request_shape,
    validate_result,
)


CLAIM_PATH = "/PlaybackPreparation/Claim"
RESULT_PATH_PREFIX = "/PlaybackPreparation/"
MAX_RESPONSE_BYTES = 64 * 1024
DEFAULT_TIMEOUT_SECONDS = 30
_STATUS_TRAILER_BYTES = 4  # LF plus curl's three-digit status code.
_MAX_CAPTURE_BYTES = MAX_RESPONSE_BYTES + _STATUS_TRAILER_BYTES


class PlaybackPreparationTransportError(RuntimeError):
    """A bounded transport or HTTP response could not be accepted."""

    def __init__(self, message: str, *, status: int | None = None):
        super().__init__(message)
        self.status = status


Transport = Callable[..., tuple[int | None, bytes]]


def validate_base_url(base: str) -> str:
    """Return a normalized numeric-loopback HTTP(S) base or reject it."""
    if not isinstance(base, str) or not base or base != base.strip():
        raise PlaybackPreparationTransportError(
            "playback-preparation base must be a direct loopback HTTP(S) URL")
    try:
        parsed = urlsplit(base)
        hostname = parsed.hostname
        port = parsed.port
    except ValueError as exc:
        raise PlaybackPreparationTransportError(
            "playback-preparation base has an invalid URL") from exc
    if (
        parsed.scheme not in {"http", "https"}
        or hostname is None
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or parsed.path not in {"", "/"}
    ):
        raise PlaybackPreparationTransportError(
            "playback-preparation base must be a direct loopback HTTP(S) URL")
    try:
        address = ipaddress.ip_address(hostname)
    except ValueError as exc:
        raise PlaybackPreparationTransportError(
            "playback-preparation base must use a numeric loopback address") from exc
    if not address.is_loopback:
        raise PlaybackPreparationTransportError(
            "playback-preparation base must use a loopback address")
    if address.version == 6:
        authority = f"[{address.compressed}]"
    else:
        authority = address.compressed
    if port is not None:
        authority += f":{port}"
    return f"{parsed.scheme}://{authority}"


def resolve_loopback_base(
    env: Mapping[str, str],
    token: str,
    *,
    request: Callable[..., Any] = jellyfin_client.curl,
) -> str | None:
    """Probe only numeric loopback candidates, with the local default first.

    The ordinary Jellyfin resolver intentionally supports an external public
    URL for read-only tooling. This worker carries an administrator token and
    therefore has a narrower transport policy: an unsafe configured URL is
    ignored, never probed, and never allowed to displace the local candidate.
    """
    candidates = ["http://127.0.0.1:8096"]
    configured = env.get("JELLYFIN_URL")
    if configured:
        try:
            configured = validate_base_url(configured)
        except PlaybackPreparationTransportError:
            configured = None
        if configured and configured not in candidates:
            candidates.append(configured)
    for base in candidates:
        if request("GET", f"{base}/System/Info/Public", token):
            return base
    return None


def _kill_and_wait(process: Any) -> None:
    """Terminate a bounded child and surface cleanup failures."""
    try:
        if process.poll() is None:
            process.kill()
    except OSError as exc:
        if process.poll() is None:
            raise PlaybackPreparationTransportError(
                "transport process cleanup failed") from exc
    process.wait()


def _run_bounded_subprocess(
    command: list[Any],
    payload: bytes,
    *,
    timeout: float,
    path: str,
) -> SimpleNamespace:
    """Run curl while retaining at most the body plus its status trailer."""
    try:
        process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            close_fds=True,
        )
    except OSError as exc:
        raise PlaybackPreparationTransportError(
            f"{path} transport could not start curl: {exc}") from exc

    output = bytearray()
    selector = selectors.DefaultSelector()
    stdout = process.stdout
    stdin = process.stdin
    deadline = time.monotonic() + timeout
    stdin_registered = False
    stdout_registered = False
    try:
        if stdout is None:
            raise PlaybackPreparationTransportError(
                f"{path} transport did not provide response output")
        os.set_blocking(stdout.fileno(), False)
        selector.register(stdout, selectors.EVENT_READ, "stdout")
        stdout_registered = True
        if stdin is not None and payload:
            os.set_blocking(stdin.fileno(), False)
            selector.register(stdin, selectors.EVENT_WRITE, "stdin")
            stdin_registered = True
        elif stdin is not None:
            stdin.close()

        payload_offset = 0
        while stdin_registered or stdout_registered:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise subprocess.TimeoutExpired(command, timeout)
            ready = selector.select(remaining)
            if not ready:
                raise subprocess.TimeoutExpired(command, timeout)

            if stdin_registered and process.poll() is not None:
                selector.unregister(stdin)
                stdin_registered = False
                stdin.close()

            for key, _events in ready:
                if key.data == "stdin" and stdin_registered:
                    try:
                        written = os.write(
                            stdin.fileno(), payload[payload_offset:])
                    except BlockingIOError:
                        continue
                    except OSError as exc:
                        raise PlaybackPreparationTransportError(
                            f"{path} transport request write failed: {exc}") from exc
                    if written <= 0:
                        raise PlaybackPreparationTransportError(
                            f"{path} transport request write made no progress")
                    payload_offset += written
                    if payload_offset == len(payload):
                        selector.unregister(stdin)
                        stdin_registered = False
                        stdin.close()
                elif key.data == "stdout" and stdout_registered:
                    try:
                        chunk = os.read(
                            stdout.fileno(), _MAX_CAPTURE_BYTES + 1 - len(output))
                    except BlockingIOError:
                        continue
                    if not chunk:
                        selector.unregister(stdout)
                        stdout_registered = False
                        continue
                    output.extend(chunk)
                    if len(output) > _MAX_CAPTURE_BYTES:
                        raise PlaybackPreparationTransportError(
                            f"{path} returned an oversized response")
        return_code = process.wait(timeout=max(0.0, deadline - time.monotonic()))
        return SimpleNamespace(returncode=return_code, stdout=bytes(output), stderr=b"")
    except OSError as exc:
        _kill_and_wait(process)
        raise PlaybackPreparationTransportError(
            f"{path} transport I/O failed: {exc}") from exc
    except (PlaybackPreparationTransportError, subprocess.TimeoutExpired):
        _kill_and_wait(process)
        raise
    finally:
        selector.close()
        if process.stdin is not None and not process.stdin.closed:
            process.stdin.close()
        if stdout is not None:
            stdout.close()


def _default_transport(
    method: str,
    base: str,
    token: str,
    path: str,
    payload: dict[str, Any] | None,
    *,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
    run: Callable[..., Any] | None = None,
) -> tuple[int | None, bytes]:
    """Call the endpoint once and preserve exact HTTP status/body.

    POSTs are deliberately never retried: Claim is a lease-bearing operation
    and Result is an idempotent server transition whose stale response must be
    observed, not hidden behind a replay.  The response is kept in memory only
    after curl's 64 KiB download cap; no temporary response file is created.
    """
    if method not in {"POST"}:
        raise ValueError(f"unsupported playback-preparation method: {method}")
    base = validate_base_url(base)
    command = [
        jellyfin_client.SAFE_CURL,
        "-sS",
        "--max-filesize", str(MAX_RESPONSE_BYTES),
        "-w", "\n%{http_code}",
        "-X", method,
        "-H", "Content-Type: application/json",
        "--data-binary", "@-",
    ]
    for header, value in jellyfin_client.auth_headers(token).items():
        command += ["-H", f"{header}: {value}"]
    try:
        if not isinstance(payload, dict):
            raise TypeError("request payload must be a JSON object")
        encoded_payload = canonical_json_bytes(payload)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"{path} request is not bounded canonical JSON: {exc}") from exc
    command.append(f"{base.rstrip('/')}{path}")
    try:
        if run is None:
            process = _run_bounded_subprocess(
                command, encoded_payload, timeout=timeout, path=path)
        else:
            process = run(
                command, input=encoded_payload, capture_output=True,
                timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        return None, b"timeout"
    if process.returncode != 0:
        return None, process.stdout or process.stderr
    body, separator, status_bytes = process.stdout.rpartition(b"\n")
    if not separator or not status_bytes.isdigit():
        return None, process.stdout
    return int(status_bytes), body


def _decode_json_response(
    path: str,
    status: int | None,
    body: bytes,
    *,
    allow_empty: bool = False,
) -> dict[str, Any] | None:
    """Decode one bounded successful JSON response, preserving failures."""
    if not isinstance(body, bytes) or len(body) > MAX_RESPONSE_BYTES:
        raise PlaybackPreparationTransportError(
            f"{path} returned an oversized response", status=status)
    if status is None:
        raise PlaybackPreparationTransportError(
            f"{path} transport failed: {body.decode(errors='replace')[:300]}")
    if status not in {200, 204}:
        raise PlaybackPreparationTransportError(
            f"{path} refused: HTTP {status}: "
            f"{body.decode(errors='replace')[:300]}", status=status)
    if status == 204:
        if body:
            raise PlaybackPreparationTransportError(
                f"{path} returned HTTP 204 with a body", status=status)
        if allow_empty:
            return None
        raise PlaybackPreparationTransportError(
            f"{path} returned HTTP 204 without a JSON body", status=status)
    if not body:
        raise PlaybackPreparationTransportError(
            f"{path} returned HTTP {status} without a JSON body", status=status)
    try:
        value = json.loads(body, object_pairs_hook=_reject_duplicate_keys)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"{path} returned malformed JSON: {exc}", status=status) from exc
    if not isinstance(value, dict):
        raise PlaybackPreparationTransportError(
            f"{path} returned a non-object JSON value", status=status)
    return value


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """Reject ambiguous last-key-wins JSON objects at the wire boundary."""
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def claim(
    base: str,
    token: str,
    *,
    transport: Transport = _default_transport,
) -> dict[str, Any] | None:
    """Claim one server-issued request, or return ``None`` for HTTP 204."""
    base = validate_base_url(base)
    status, body = transport("POST", base, token, CLAIM_PATH, {}, timeout=DEFAULT_TIMEOUT_SECONDS)
    return _decode_json_response(CLAIM_PATH, status, body, allow_empty=True)


def renew_request(
    base: str,
    token: str,
    request: dict[str, Any],
    *,
    transport: Transport = _default_transport,
) -> None:
    """Renew the lease for one bound request without changing its body."""
    base = validate_base_url(base)
    try:
        validate_request_shape(request)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"renew request is not a valid playback-preparation contract: {exc}") from exc
    path = f"{RESULT_PATH_PREFIX}{request['requestId']}/Renew"
    status, body = transport(
        "POST", base, token, path, request, timeout=DEFAULT_TIMEOUT_SECONDS)
    if status != 204 or body:
        raise PlaybackPreparationTransportError(
            f"{path} expected an empty HTTP 204 response", status=status)


def recheck_request(
    base: str,
    token: str,
    request: dict[str, Any],
    *,
    transport: Transport = _default_transport,
) -> dict[str, Any]:
    """Rebind a claimed request to the server's current item/path state."""
    base = validate_base_url(base)
    try:
        validate_request_shape(request)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"recheck request is not a valid playback-preparation contract: {exc}") from exc
    path = f"{RESULT_PATH_PREFIX}{request['requestId']}/Recheck"
    status, body = transport(
        "POST", base, token, path, request, timeout=DEFAULT_TIMEOUT_SECONDS)
    rebound = _decode_json_response(path, status, body)
    if rebound is None:
        raise PlaybackPreparationTransportError(
            f"{path} returned no rebound request", status=status)
    try:
        validate_request_shape(rebound)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"{path} returned an invalid rebound request: {exc}",
            status=status,
        ) from exc
    if rebound["requestId"] != request["requestId"]:
        raise PlaybackPreparationTransportError(
            f"{path} returned a request bound to another requestId",
            status=status,
        )
    return rebound


def submit_result(
    base: str,
    token: str,
    result: dict[str, Any],
    *,
    transport: Transport = _default_transport,
) -> None:
    """Submit one exact result; 200/204 succeed, 409 remains observable."""
    base = validate_base_url(base)
    try:
        # The request binding is echoed in the result.  Shape-validating the
        # result against itself checks every binding and outcome before the
        # request ID is interpolated into the route; filesystem authority is
        # intentionally a server-side current-object check at submission.
        validate_result(result, result)
    except (TypeError, ValueError) as exc:
        raise PlaybackPreparationTransportError(
            f"result is not a valid playback-preparation contract: {exc}") from exc
    request_id = result["requestId"]
    path = f"{RESULT_PATH_PREFIX}{request_id}/Result"
    status, body = transport(
        "POST", base, token, path, result, timeout=DEFAULT_TIMEOUT_SECONDS)
    _decode_json_response(path, status, body, allow_empty=True)
