# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Versioned, read-only contract for playback preparation planning.

This module deliberately stops at planning.  It does not create spool files,
write media, update a database, or call the server.  A future server adapter
can use the validated documents as an immutable request/result boundary.
"""

from __future__ import annotations

import json
import math
import re
import secrets
import stat
import uuid
from pathlib import Path
from typing import Any, Callable, Mapping

from lib.transcode_work_plan import StreamingWorkPlan, plan_streaming_work


CONTRACT_TYPE = "sloptank.playback-preparation"
CONTRACT_VERSION = 1
PLANNER_ALGORITHM_VERSION = "streaming-work-plan/v1"

MAX_DOCUMENT_BYTES = 64 * 1024
MAX_PATH_BYTES = 4096
MAX_STRING_BYTES = 16 * 1024
MAX_COLLECTION_ITEMS = 256
MAX_NESTING = 8

OUTCOMES = frozenset({
    "compatible",
    "needs_preparation",
    "unprobeable",
    "refused",
    "error",
})
REASON_CODES = {
    "compatible": frozenset({"already_compatible"}),
    "needs_preparation": frozenset({"work_required"}),
    "unprobeable": frozenset({"planner_unprobeable"}),
    "refused": frozenset({"contract_refused", "authority_refused"}),
    "error": frozenset({"planner_error"}),
}

_HEX_RE = re.compile(r"^[0-9a-f]+$")
_SUFFIX_RE = re.compile(r"^\.[A-Za-z0-9]{1,16}$")
_REQUIRED_BINDINGS = (
    "contractType",
    "contractVersion",
    "plannerAlgorithmVersion",
    "requestId",
    "nonce",
    "itemId",
    "path",
    "mediaSignature",
)


class ContractValidationError(ValueError):
    """Raised when a contract document cannot be safely accepted."""


def canonical_json_bytes(document: Mapping[str, Any]) -> bytes:
    """Return bounded, deterministic JSON bytes for a contract document."""
    if not isinstance(document, dict):
        raise ContractValidationError("contract root must be an object")
    _validate_json_shape(document, depth=0)
    try:
        encoded = json.dumps(
            document,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise ContractValidationError("contract is not canonical JSON") from exc
    if len(encoded) > MAX_DOCUMENT_BYTES:
        raise ContractValidationError("contract exceeds maximum document size")
    return encoded


def make_request(
    item_id: str,
    path: str | Path,
    media_signature: str,
    *,
    request_id: str | None = None,
    nonce: str | None = None,
) -> dict[str, Any]:
    """Build and validate a request envelope without performing any writes."""
    request = {
        "contractType": CONTRACT_TYPE,
        "contractVersion": CONTRACT_VERSION,
        "plannerAlgorithmVersion": PLANNER_ALGORITHM_VERSION,
        "requestId": request_id or str(uuid.uuid4()),
        "nonce": nonce or secrets.token_hex(16),
        "itemId": item_id,
        "path": str(path),
        "mediaSignature": media_signature,
    }
    validate_request(request)
    return request


def validate_request(request: Mapping[str, Any]) -> dict[str, Any]:
    """Validate a request and its current filesystem authority."""
    request = validate_request_shape(request)
    _validate_media_path_authority(request["path"])
    return request


def validate_request_shape(request: Mapping[str, Any]) -> dict[str, Any]:
    """Validate immutable request fields without requiring the file to remain.

    Planning requires a currently existing regular file, which is enforced by
    :func:`validate_request`.  Result construction happens after that read and
    must still be able to report a bound failure if the file disappears or is
    replaced, so result validation only checks the absolute path string.
    """
    if not isinstance(request, dict):
        raise ContractValidationError("request must be an object")
    canonical_json_bytes(request)
    _validate_bindings(request)
    _validate_media_path_string(request["path"])
    return request


def result_for_plan(
    request: Mapping[str, Any],
    plan: StreamingWorkPlan | None,
) -> dict[str, Any]:
    """Create a result bound to *request* from the pure planner outcome."""
    request = validate_request_shape(request)
    if plan is None:
        outcome = "unprobeable"
        reason_code = "planner_unprobeable"
        wire_plan = None
    else:
        outcome = "needs_preparation" if plan.needed else "compatible"
        reason_code = "work_required" if plan.needed else "already_compatible"
        wire_plan = serialize_plan(plan)
    result = dict(request)
    result.update({
        "outcome": outcome,
        "reasonCode": reason_code,
        "plan": wire_plan,
    })
    validate_result(request, result)
    return result


def planner_error_result(request: Mapping[str, Any]) -> dict[str, Any]:
    """Create a bound fail-closed result for an unexpected planner failure.

    Diagnostic exception text is intentionally not placed on the contract
    wire.  The worker can log it locally, while the server receives only the
    stable ``error``/``planner_error`` outcome and the exact request binding.
    """
    request = validate_request_shape(request)
    result = dict(request)
    result.update({
        "outcome": "error",
        "reasonCode": "planner_error",
        "plan": None,
    })
    validate_result(request, result)
    return result


def evaluate_request(
    request: Mapping[str, Any],
    *,
    info: dict | None = None,
    planner: Callable[..., StreamingWorkPlan | None] | None = None,
) -> dict[str, Any]:
    """Run the existing planner through the validated, read-only boundary.

    ``info`` is an injected probe result for tests and controlled callers.  An
    unexpected planner exception deliberately propagates; silently turning a
    coding or I/O failure into a compatible result would violate fail-closed
    behavior.  A normal planner ``None`` is represented as ``unprobeable``.
    """
    request = validate_request(request)
    planner = planner or plan_streaming_work
    plan = planner(Path(request["path"]), info=info)
    return result_for_plan(request, plan)


def validate_result(
    request: Mapping[str, Any],
    result: Mapping[str, Any],
) -> dict[str, Any]:
    """Validate a result and every request binding it must echo exactly."""
    request = validate_request_shape(request)
    if not isinstance(result, dict):
        raise ContractValidationError("result must be an object")
    result = validate_request_shape(result)
    for field in _REQUIRED_BINDINGS:
        if result[field] != request[field]:
            raise ContractValidationError(f"result binding mismatch: {field}")

    outcome = result.get("outcome")
    reason_code = result.get("reasonCode")
    if outcome not in OUTCOMES:
        raise ContractValidationError("unknown result outcome")
    if reason_code not in REASON_CODES[outcome]:
        raise ContractValidationError("unknown or invalid result reason code")

    has_plan = result.get("plan") is not None
    if outcome in {"compatible", "needs_preparation"} and not has_plan:
        raise ContractValidationError("planned outcome is missing its plan")
    if outcome in {"unprobeable", "refused", "error"} and has_plan:
        raise ContractValidationError("non-planned outcome contains a plan")
    if has_plan:
        _validate_plan(result["plan"])
    return result


def serialize_plan(plan: StreamingWorkPlan) -> dict[str, Any]:
    """Convert the planner dataclass into its stable wire representation."""
    if not isinstance(plan, StreamingWorkPlan):
        raise ContractValidationError("plan must be a StreamingWorkPlan")
    return {
        "targetSuffix": plan.target_suffix,
        "needsVideo": plan.needs_video,
        "needsAudio": plan.needs_audio,
        "needsContainer": plan.needs_container,
        "needsFaststart": plan.needs_faststart,
        "addedAudio": [
            {
                "sourceIndex": track.source_index,
                "language": track.language,
                "channels": track.channels,
                "bitrate": track.bitrate,
                "makeDefault": track.make_default,
                "isCommentary": track.is_commentary,
                "applyLoudnorm": track.apply_loudnorm,
            }
            for track in plan.added_audio
        ],
        "loudnessMeasuredSourceIndexes": sorted(
            plan.loudness_measured_source_indexes
        ),
    }


def _validate_bindings(document: Mapping[str, Any]) -> None:
    missing = [field for field in _REQUIRED_BINDINGS if field not in document]
    if missing:
        raise ContractValidationError(f"missing contract fields: {missing}")
    if document["contractType"] != CONTRACT_TYPE:
        raise ContractValidationError("unknown contract type")
    if document["contractVersion"] != CONTRACT_VERSION:
        raise ContractValidationError("unsupported contract version")
    if document["plannerAlgorithmVersion"] != PLANNER_ALGORITHM_VERSION:
        raise ContractValidationError("unsupported planner algorithm version")
    _validate_uuid(document["requestId"], "requestId")
    nonce = document["nonce"]
    if (
        not isinstance(nonce, str)
        or len(nonce) < 32
        or len(nonce) % 2
        or len(nonce) > 256
        or not _HEX_RE.fullmatch(nonce)
        or int(nonce, 16) == 0
    ):
        raise ContractValidationError("nonce must contain at least 128 random bits")
    _validate_uuid(document["itemId"], "itemId")
    _validate_string(
        document["path"], "path", max_bytes=MAX_PATH_BYTES, nonempty=True
    )
    _validate_string(
        document["mediaSignature"], "mediaSignature", max_bytes=1024, nonempty=True
    )


def _validate_media_path_string(path_value: Any) -> None:
    if not isinstance(path_value, str):
        raise ContractValidationError("path must be a string")
    path = Path(path_value)
    if not path.is_absolute():
        raise ContractValidationError("path must be absolute")


def _validate_media_path_authority(path_value: Any) -> None:
    _validate_media_path_string(path_value)
    path = Path(path_value)
    try:
        path_stat = path.lstat()
    except OSError as exc:
        raise ContractValidationError("path cannot be inspected") from exc
    if path.is_symlink():
        raise ContractValidationError("path must not be a symlink")
    if not path.is_file() or not stat.S_ISREG(path_stat.st_mode):
        raise ContractValidationError("path must be a regular file")


def _validate_uuid(value: Any, field: str) -> None:
    if not isinstance(value, str):
        raise ContractValidationError(f"{field} must be a canonical UUID")
    try:
        parsed = uuid.UUID(value)
    except ValueError as exc:
        raise ContractValidationError(f"{field} must be a canonical UUID") from exc
    if str(parsed) != value or parsed.int == 0:
        raise ContractValidationError(f"{field} must be a canonical non-nil UUID")


def _validate_plan(plan: Any) -> None:
    if not isinstance(plan, dict):
        raise ContractValidationError("plan must be an object")
    required = (
        "targetSuffix", "needsVideo", "needsAudio", "needsContainer",
        "needsFaststart", "addedAudio", "loudnessMeasuredSourceIndexes",
    )
    if any(field not in plan for field in required):
        raise ContractValidationError("plan is missing required fields")
    _validate_string(plan["targetSuffix"], "targetSuffix", max_bytes=32, nonempty=True)
    if not _SUFFIX_RE.fullmatch(plan["targetSuffix"]):
        raise ContractValidationError("targetSuffix must be a simple filename suffix")
    for field in required[1:5]:
        if type(plan[field]) is not bool:
            raise ContractValidationError(f"{field} must be boolean")
    additions = plan["addedAudio"]
    if not isinstance(additions, list) or len(additions) > 32:
        raise ContractValidationError("addedAudio must be a bounded list")
    for addition in additions:
        if not isinstance(addition, dict):
            raise ContractValidationError("audio plan entry must be an object")
        required_audio = (
            "sourceIndex", "language", "channels", "bitrate", "makeDefault",
            "isCommentary", "applyLoudnorm",
        )
        if any(field not in addition for field in required_audio):
            raise ContractValidationError("audio plan entry is incomplete")
        if (
            type(addition["sourceIndex"]) is not int
            or addition["sourceIndex"] < 0
            or type(addition["channels"]) is not int
            or addition["channels"] <= 0
        ):
            raise ContractValidationError("audio plan indexes/channels are invalid")
        _validate_string(addition["language"], "audio language", max_bytes=64)
        _validate_string(addition["bitrate"], "audio bitrate", max_bytes=32)
        for field in required_audio[4:]:
            if type(addition[field]) is not bool:
                raise ContractValidationError(f"{field} must be boolean")
    measured = plan["loudnessMeasuredSourceIndexes"]
    if not isinstance(measured, list) or len(measured) > 32:
        raise ContractValidationError("loudness indexes must be a bounded list")
    if any(type(index) is not int or index < 0 for index in measured):
        raise ContractValidationError("loudness indexes are invalid")
    if measured != sorted(set(measured)):
        raise ContractValidationError("loudness indexes must be sorted and unique")


def _validate_string(
    value: Any,
    field: str,
    *,
    max_bytes: int = MAX_STRING_BYTES,
    nonempty: bool = False,
) -> None:
    if not isinstance(value, str) or (nonempty and not value):
        raise ContractValidationError(f"{field} must be a string")
    try:
        size = len(value.encode("utf-8"))
    except UnicodeError as exc:
        raise ContractValidationError(f"{field} is not valid UTF-8") from exc
    if size > max_bytes:
        raise ContractValidationError(f"{field} exceeds its maximum size")


def _validate_json_shape(value: Any, *, depth: int) -> None:
    if depth > MAX_NESTING:
        raise ContractValidationError("contract nesting is too deep")
    if value is None or type(value) is bool or type(value) is int:
        if type(value) is int and len(str(abs(value))) > 32:
            raise ContractValidationError("integer exceeds maximum size")
        return
    if type(value) is float:
        if not math.isfinite(value):
            raise ContractValidationError("non-finite numbers are forbidden")
        return
    if isinstance(value, str):
        _validate_string(value, "contract string")
        return
    if isinstance(value, list):
        if len(value) > MAX_COLLECTION_ITEMS:
            raise ContractValidationError("contract list is too large")
        for item in value:
            _validate_json_shape(item, depth=depth + 1)
        return
    if isinstance(value, dict):
        if len(value) > MAX_COLLECTION_ITEMS:
            raise ContractValidationError("contract object is too large")
        for key, item in value.items():
            if not isinstance(key, str):
                raise ContractValidationError("contract keys must be strings")
            _validate_string(key, "contract key")
            _validate_json_shape(item, depth=depth + 1)
        return
    raise ContractValidationError("contract contains a non-JSON value")
