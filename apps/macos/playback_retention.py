# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Acceptance assertions for played-item history and issued links across app replacement."""

from __future__ import annotations

import datetime as dt
from typing import Any
import urllib.parse

from smoke_deadline import Deadline, SmokeError
from smoke_server_api import json_api


def _played_state(client: dict[str, str], deadline: Deadline) -> dict[str, Any]:
    item = json_api(client["base"], "GET", f"/Users/{client['user_id']}/Items/{client['item_id']}",
                    deadline, token=client["token"])
    data = item.get("UserData")
    if not isinstance(data, dict) or int(data.get("PlayCount") or 0) < 1 or not data.get("LastPlayedDate"):
        raise SmokeError(f"UI-played item has no retained playback history: {data!r}")
    try:
        dt.datetime.fromisoformat(data["LastPlayedDate"])
    except (TypeError, ValueError) as error:
        raise SmokeError(f"Playback history has an invalid last-played date: {data!r}: {error}") from error
    return {"play_count": int(data["PlayCount"]), "last_played_date": data["LastPlayedDate"],
            "position_ticks": int(data.get("PlaybackPositionTicks") or 0)}


def _require_resolution(client: dict[str, str], permalink_id: str, deadline: Deadline) -> None:
    result = json_api(client["base"], "GET", "/Permalinks/" + urllib.parse.quote(permalink_id, safe="") + "/Items",
                      deadline, token=client["token"])
    items = result.get("Items")
    if not isinstance(items, list) or not any(isinstance(row, dict) and str(row.get("Id")) == client["item_id"] for row in items):
        raise SmokeError(f"Issued permalink {permalink_id} did not resolve to the retained item: {result!r}")


def capture_playback_state(client: dict[str, str], deadline: Deadline) -> dict[str, Any]:
    """Issue one link for the browser-played fixture and prove its pre-replacement state."""
    before = _played_state(client, deadline)
    issued = json_api(client["base"], "POST", f"/Items/{client['item_id']}/Permalink",
                      deadline, token=client["token"])
    alias = issued.get("CanonicalId")
    if issued.get("Version") != 1 or not isinstance(alias, str) or not alias:
        raise SmokeError(f"Permalink issue response omitted its versioned canonical alias: {issued!r}")
    _require_resolution(client, alias, deadline)
    return {"item_id": client["item_id"], "permalink_id": alias, "history": before}


def verify_playback_state(client: dict[str, str], expected: dict[str, Any], deadline: Deadline) -> dict[str, Any]:
    """Resolve the original alias without reissuing it and reject lost playback history."""
    if client["item_id"] != expected["item_id"]:
        raise SmokeError(f"App replacement changed the played item: {expected['item_id']} -> {client['item_id']}")
    before = expected["history"]
    failures = []
    try:
        after = _played_state(client, deadline)
        date_retained = dt.datetime.fromisoformat(after["last_played_date"]) >= dt.datetime.fromisoformat(before["last_played_date"])
        if after["play_count"] < before["play_count"] or not date_retained:
            raise SmokeError(f"App replacement lost playback history: {before!r} -> {after!r}")
    except SmokeError as error:
        failures.append(str(error))
    except (TypeError, ValueError) as error:
        failures.append(f"Playback history dates cannot be compared: {before!r}: {error}")
    try:
        _require_resolution(client, expected["permalink_id"], deadline)
    except SmokeError as error:
        failures.append(str(error))
    if failures:
        raise SmokeError("Playback retention checks failed: " + "; ".join(failures))
    return {"status": "passed", "item_id": client["item_id"], "permalink_id": expected["permalink_id"],
            "before": before, "after": after, "original_permalink_resolved": True}
