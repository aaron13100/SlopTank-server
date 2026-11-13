# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""HTTP request and JSON response boundary for installed-server acceptance clients."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any

from smoke_deadline import Deadline, SmokeError


def api(
    base: str,
    method: str,
    path: str,
    deadline: Deadline,
    *,
    body: dict[str, Any] | None = None,
    token: str | None = None,
    timeout: float = 30,
) -> tuple[int, bytes, dict[str, str]]:
    headers = {
        "Accept": "application/json",
        "Authorization": ('MediaBrowser Client="SlopTank Installed App Smoke", Device="Smoke", '
                          'DeviceId="installed-app-smoke", Version="1.0"'),
    }
    payload = json.dumps(body).encode() if body is not None else None
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] += f', Token="{token}"'
    request = urllib.request.Request(base.rstrip("/") + path, data=payload, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=deadline.timeout(timeout, f"{method} {path}")) as response:
            return response.status, response.read(), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise SmokeError(f"{method} {path}: {error}") from error


def json_api(base: str, method: str, path: str, deadline: Deadline, *,
             body: dict[str, Any] | None = None, token: str | None = None,
             timeout: float = 30) -> dict[str, Any]:
    status, data, _ = api(base, method, path, deadline, body=body, token=token, timeout=timeout)
    if not 200 <= status < 300:
        raise SmokeError(f"{method} {path}: HTTP {status}: {data[:2000].decode(errors='replace')}")
    try:
        value = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SmokeError(f"{method} {path}: invalid JSON response {data[:500]!r}") from error
    if not isinstance(value, dict):
        raise SmokeError(f"{method} {path}: expected JSON object, got {type(value).__name__}")
    return value
