# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""One bounded playback-preparation worker cycle."""

from __future__ import annotations

import logging
import threading
from pathlib import Path
from typing import Any, Callable

from lib.playback_contract import (
    ContractValidationError,
    evaluate_request,
    planner_error_result,
    validate_request,
    validate_request_shape,
)
from lib.playback_preparation_transport import (
    claim,
    recheck_request as recheck_bound_request,
    renew_request as renew_bound_request,
    submit_result,
)
from lib.tv_audio_catchup_worker import prepare_audio_before_video_conversion
from lib.video_post_process import _post_process_video

log = logging.getLogger(__name__)


class PlaybackPreparationWorkerError(RuntimeError):
    """A claimed request was malformed and cannot be safely classified."""


LEASE_SECONDS = 30 * 60
LEASE_RENEW_INTERVAL_SECONDS = LEASE_SECONDS / 3


class _LeaseRenewer:
    """Renew one immutable claimed request until preparation has stopped."""

    def __init__(self, request: dict[str, Any], renew: Callable[[dict[str, Any]], None],
                 interval: float):
        if interval <= 0:
            raise ValueError("lease renew interval must be positive")
        self._request = request
        self._renew = renew
        self._interval = interval
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._error: Exception | None = None

    def _run(self) -> None:
        while not self._stop.wait(self._interval):
            try:
                self._renew(self._request)
            except Exception as exc:  # noqa: BLE001 - surface via stop()
                self._error = exc
                log.exception(
                    "lease renewal failed for playback request %s",
                    self._request["requestId"],
                )
                self._stop.set()
                return

    def start(self) -> None:
        self._thread = threading.Thread(
            target=self._run,
            name="playback-preparation-lease-renewer",
            daemon=True,
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join()
        if self._error is not None:
            raise PlaybackPreparationWorkerError(
                f"playback request lease renewal failed: {self._error}") from self._error


def _audio_only_plan(plan: Any) -> bool:
    """Whether the wire plan is solvable by the shared audio preparation path."""
    return (
        isinstance(plan, dict)
        and not plan["needsVideo"]
        and not plan["needsContainer"]
        and not plan["needsFaststart"]
        and (plan["needsAudio"] or bool(plan["addedAudio"]))
    )


def _summary(result: dict[str, Any], *, error: Exception | None = None) -> dict[str, Any]:
    summary = {
        "status": "submitted",
        "requestId": result["requestId"],
        "outcome": result["outcome"],
    }
    if "reasonCode" in result:
        summary["reasonCode"] = result["reasonCode"]
    if error is not None:
        summary["error"] = str(error)
    return summary


def _best_effort_recheck(
        request: dict[str, Any],
        recheck: Callable[[dict[str, Any]], dict[str, Any]] | None,
        *,
        context: str,
) -> dict[str, Any]:
    """Return a validated rebound request, or the original immutable binding."""
    if recheck is None:
        return request
    try:
        rebound = recheck(request)
        validate_request_shape(rebound)
        if rebound["requestId"] != request["requestId"]:
            raise ContractValidationError("recheck changed requestId")
        return rebound
    except Exception:  # noqa: BLE001 - planner_error remains the safe outcome
        log.exception("best-effort %s recheck failed for %s", context, request["requestId"])
        return request


def _submit_planner_error(
        request: dict[str, Any], submit: Callable[[dict[str, Any]], None],
        exc: Exception) -> dict[str, Any]:
    result = planner_error_result(request)
    submit(result)
    return _summary(result, error=exc)


def run_one_claim(
    *,
    claim_request: Callable[[], dict[str, Any] | None],
    submit: Callable[[dict[str, Any]], None],
    planner: Callable[..., Any] | None = None,
    info: dict | None = None,
    renew_request: Callable[[dict[str, Any]], None] | None = None,
    recheck_request: Callable[[dict[str, Any]], dict[str, Any]] | None = None,
    prepare_audio: Callable[..., Any] = prepare_audio_before_video_conversion,
    post_process: Callable[..., Any] = _post_process_video,
    renew_interval: float = LEASE_RENEW_INTERVAL_SECONDS,
) -> dict[str, Any]:
    """Claim, classify, prepare when needed, and submit one request cycle.

    A malformed server request is rejected without submission because no safe
    binding exists.  An exception from the existing planner is different: the
    request itself is valid, so a bound ``planner_error`` result is submitted
    and the exception is retained in local logs.  A preparation result is
    renewed while media writes run, then rebound and classified again before
    the final result is submitted.
    """
    request = claim_request()
    if request is None:
        return {"status": "no_work"}
    try:
        validate_request(request)
    except ContractValidationError as exc:
        raise PlaybackPreparationWorkerError(
            f"server issued an invalid playback-preparation request: {exc}") from exc
    try:
        result = evaluate_request(request, info=info, planner=planner)
    except Exception as exc:  # noqa: BLE001 - classify every planner failure
        log.exception("planner failed for playback request %s", request["requestId"])
        return _submit_planner_error(request, submit, exc)
    submit(result)
    if result["outcome"] != "needs_preparation":
        return _summary(result)

    if renew_request is None or recheck_request is None:
        exc = PlaybackPreparationWorkerError(
            "preparation requires injectable lease-renew and recheck callbacks")
        log.error("cannot prepare playback request %s: %s", request["requestId"], exc)
        rebound = _best_effort_recheck(request, recheck_request, context="missing-seam")
        return _submit_planner_error(rebound, submit, exc)

    preparation = prepare_audio if _audio_only_plan(result.get("plan")) else post_process
    try:
        lease = _LeaseRenewer(request, renew_request, renew_interval)
        lease.start()
        try:
            preparation(Path(request["path"]))
        finally:
            lease.stop()
    except Exception as exc:  # noqa: BLE001 - convert all prep failures to retryable error
        log.exception(
            "preparation failed for playback request %s", request["requestId"])
        rebound = _best_effort_recheck(request, recheck_request, context="preparation-error")
        return _submit_planner_error(rebound, submit, exc)

    rebound: dict[str, Any] = request
    try:
        candidate = recheck_request(request)
        validate_request_shape(candidate)
        if candidate["requestId"] != request["requestId"]:
            raise ContractValidationError("recheck changed requestId")
        rebound = candidate
        final = evaluate_request(rebound, info=info, planner=planner)
    except Exception as exc:  # noqa: BLE001 - never report a failed prep as compatible
        log.exception(
            "final playback classification failed for request %s", request["requestId"])
        return _submit_planner_error(rebound, submit, exc)
    submit(final)
    return _summary(final)


def run_one_claim_from_server(base: str, token: str) -> dict[str, Any]:
    """Run one production transport cycle against the configured server."""
    return run_one_claim(
        claim_request=lambda: claim(base, token),
        submit=lambda result: submit_result(base, token, result),
        renew_request=lambda request: renew_bound_request(base, token, request),
        recheck_request=lambda request: recheck_bound_request(base, token, request),
    )
