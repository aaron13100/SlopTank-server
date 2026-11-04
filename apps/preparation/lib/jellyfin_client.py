#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Jellyfin API access via tools/jellyfin-safe-curl.sh (method-whitelisted, no DELETE).

Shared by any tools/ script that needs read-mostly access to the Jellyfin REST
API: env loading, base-URL discovery (external DuckDNS name first, localhost
fallbacks second), and the handful of endpoints used for library auditing.
"""

from __future__ import annotations

import datetime
import json
import logging
import os
import subprocess
import time
from collections.abc import Mapping
from pathlib import Path
from typing import Any, Callable, Optional
from urllib.parse import urlsplit

if __package__:
    from . import jellyfin_scheduled_tasks as _scheduled
else:
    from lib import jellyfin_scheduled_tasks as _scheduled

ROOT = str(Path(__file__).resolve().parents[2])
ENV_FILE = os.path.join(ROOT, "tools", "jellyfin.env")
SAFE_CURL = os.path.join(ROOT, "tools", "jellyfin-safe-curl.sh")

# Candidate base URLs tried in order; the env URL is an external DuckDNS name that
# does not resolve from the host itself, so localhost fallbacks are essential.
FALLBACK_URLS = ["http://127.0.0.1:8096", "http://localhost:8096"]

log = logging.getLogger(__name__)


def load_env(*, env_file: str | os.PathLike[str] = ENV_FILE,
             environ: Mapping[str, str] | None = None) -> dict[str, str]:
    """Parse Jellyfin configuration, with process-local overrides.

    Scheduled wrappers source credentials from ignored local files and export
    them rather than copying secrets into another file.  Let those exported
    values override ``tools/jellyfin.env`` while retaining that file as the
    normal source for the URL and admin API key.
    """
    env: dict[str, str] = {}
    if environ is None:
        environ = os.environ
    if os.path.exists(env_file):
        with open(env_file) as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, _, val = line.partition("=")
                env[key.strip()] = val.strip().strip('"').strip("'")
    for key in (
        "JELLYFIN_URL",
        "JELLYFIN_TOKEN",
        "JELLYFIN_USER_TOKEN",
        "JELLYFIN_USERNAME",
        "JELLYFIN_PASSWORD",
    ):
        if key in environ:
            env[key] = environ[key]
    return env


def auth_headers(token: str) -> dict[str, str]:
    """The one place any tool builds Jellyfin authentication.

    Use the Authorization header, NOT `X-Emby-Token`. This server disabled the
    legacy header scheme on 2026-05-31 and answers 401 to it, which is silent
    in every caller here because they all treat a failed call as best-effort.
    Verified 2026-09-03 against /ScheduledTasks: X-Emby-Token -> 401,
    Authorization -> 200, and a deliberately wrong token -> 401 (so the 200 is
    real authentication, not an unauthenticated endpoint).

    That was not theoretical: the library rescan the backfill fires after
    replacing files had been failing this way, so converted .mp4 files never
    entered the library and Jellyfin kept serving paths to .mkv files that no
    longer existed.

    Preferred over the `?ApiKey=` query parameter, which also works, because a
    token in a URL ends up in access logs and proxy history.

    :param token: The Jellyfin API token.
    :returns: Headers to merge into any request.
    """
    return {
        "Authorization": (
            f'MediaBrowser Token="{token}", Client="mediaserver-tools", '
            'Device="mediaserver", DeviceId="mediaserver-tools", Version="1"'
        )
    }


def authenticate_user(base: str, username: str, password: str, *,
                      request: Callable[..., Any] | None = None) -> Optional[str]:
    """Authenticate a Jellyfin user and return its access token.

    API keys have no user claim.  Endpoints whose authorization is scoped to
    a library user (including the protected permalink rewrite operation) must
    therefore use a user access token even when the configured API key is an
    administrator key.  Authentication itself uses the normal Jellyfin client
    header with an empty token, as required by ``AuthenticateByName``.

    Returns ``None`` on a transport, HTTP, or response-shape failure so callers
    can fail closed before replacing media.
    """
    if request is None:
        request = curl
    body = json.dumps({"Username": username, "Pw": password}).encode("utf-8")
    response = request(
        "POST", f"{base.rstrip('/')}/Users/AuthenticateByName", "", body=body)
    if not response:
        return None
    try:
        token = json.loads(response).get("AccessToken")
    except (ValueError, TypeError):
        return None
    return str(token) if token else None


def curl(method: str, url: str, token: str, body: Optional[bytes] = None,
         retries: int = 2) -> Optional[bytes]:
    """Call the Jellyfin API through jellyfin-safe-curl.sh (method-whitelisted, no DELETE).

    Returns raw response bytes, or None on transport/HTTP failure. ``body`` is sent
    as the request payload with Content-Type application/json when provided. GET
    requests are retried on transient empty/error responses (this host occasionally
    drops a connection mid-scan); mutating calls are issued once.

    An empty body on a successful mutating call is returned as b"" (many Jellyfin
    write endpoints answer 204 No Content); only a transport/HTTP error yields
    None. Conflating the two made every successful RemoteSearch/Apply and
    scan-trigger POST report as failed (2026-07-22 incident).
    """
    cmd = [SAFE_CURL, "-fsS"]
    for header, value in auth_headers(token).items():
        cmd += ["-H", f"{header}: {value}"]
    if method != "GET":
        cmd += ["-X", method]
    if body is not None:
        cmd += ["-H", "Content-Type: application/json", "-d", "@-"]
    cmd.append(url)
    attempts = retries if method == "GET" else 1
    timeout_seconds = 120
    endpoint_path = urlsplit(url).path or "/"
    for _ in range(attempts):
        try:
            proc = subprocess.run(
                cmd, input=body, capture_output=True, timeout=timeout_seconds)
        except subprocess.TimeoutExpired as exc:
            stderr = exc.stderr or b""
            if isinstance(stderr, bytes):
                stderr = stderr.decode("utf-8", errors="replace")
            else:
                stderr = str(stderr)
            stderr = stderr.replace(url, endpoint_path)
            if token:
                stderr = stderr.replace(token, "[redacted]")
            log.warning(
                "Jellyfin curl %s %s timed out after %ss: %s",
                method, endpoint_path, timeout_seconds,
                stderr.strip() or "no stderr output")
            continue
        if proc.returncode == 0:
            # GET: an empty body is a transient drop -> retry; writes: 204 is success.
            if proc.stdout or method != "GET":
                return proc.stdout
        else:
            stderr = proc.stderr or b""
            if isinstance(stderr, bytes):
                stderr = stderr.decode("utf-8", errors="replace")
            else:
                stderr = str(stderr)
            stderr = stderr.replace(url, endpoint_path)
            if token:
                stderr = stderr.replace(token, "[redacted]")
            log.warning(
                "Jellyfin curl %s %s failed (returncode=%s): %s",
                method, endpoint_path, proc.returncode,
                stderr.strip() or "no stderr output")
    return None


class LibraryFetchError(Exception):
    """A Jellyfin item/library query could not be confirmed; the transport failed.

    The same None-vs-real-answer distinction curl() documents, raised as a
    typed error for callers that list items: an empty/partial list on a
    dropped connection would let "no answer" read as "nothing here" (the
    fold that cost three permalinked titles a wasted remux each, 2026-09-19).

    :ivar url: The request URL that failed, for diagnosis.
    """

    def __init__(self, message: str, *, url: str):
        super().__init__(message)
        self.url = url


def resolve_base_url(env: dict[str, str], token: str, *,
                     request: Callable[..., Any] = curl) -> Optional[str]:
    """Find the first base URL that answers /System/Info/Public. Returns it or None.

    :param request: The HTTP transport, for tests. Same call contract as curl.
    """
    candidates = []
    if env.get("JELLYFIN_URL"):
        candidates.append(env["JELLYFIN_URL"].rstrip("/"))
    candidates += FALLBACK_URLS
    for url in candidates:
        out = request("GET", f"{url}/System/Info/Public", token)
        if out:
            return url
    return None


def _fetch_items(base: str, token: str, uid: str, item_type: str, fields: str,
                  *, request: Callable[..., Any] = curl) -> list[dict[str, Any]]:
    """Return all items of one type with the requested fields.

    Shared by fetch_movies/fetch_episodes/fetch_series so a query-shape fix
    (paging, field list separator, etc.) lands once instead of three times.
    """
    url = (
        f"{base}/Users/{uid}/Items"
        f"?Recursive=true&IncludeItemTypes={item_type}"
        f"&fields={fields}"
        "&EnableImageTypes=Primary&Limit=10000"
    )
    out = request("GET", url, token)
    if not out:
        return []
    return json.loads(out).get("Items", [])


def fetch_movies(base: str, token: str, uid: str,
                  *, request: Callable[..., Any] = curl) -> list[dict[str, Any]]:
    """Return all Movie items with identity and primary-image fields.

    PrimaryImageAspectRatio/ImageTags feed the degraded-artwork check: a movie
    primary wider than portrait is a Screen Grabber video frame, not a poster.
    """
    return _fetch_items(
        base, token, uid, "Movie",
        "Path,ProviderIds,ProductionYear,OriginalTitle,PrimaryImageAspectRatio,MediaSources",
        request=request)


def fetch_episodes(base: str, token: str, uid: str,
                    *, request: Callable[..., Any] = curl) -> list[dict[str, Any]]:
    """Return all Episode items with their path and parent series id.

    Used to resolve an episode's SERIES-level provider ids (e.g. for
    subtitle-search imdb ids, see subliminal_jellyfin_refiner.py): episodes
    are rarely catalogued with their own imdb entry, so the caller looks up
    SeriesId in fetch_series()'s result rather than trusting ProviderIds
    here.
    """
    return _fetch_items(base, token, uid, "Episode", "Path,SeriesId,MediaSources", request=request)


def fetch_series(base: str, token: str, uid: str,
                  *, request: Callable[..., Any] = curl) -> list[dict[str, Any]]:
    """Return Series items with provider identity and top-level path fields."""
    return _fetch_items(
        base, token, uid, "Series",
        "Path,ProviderIds,ProductionYear,OriginalTitle",
        request=request)


class LibraryPathUpdateError(RuntimeError):
    """A moved file could not be matched to and updated as one library item."""


def library_item_at_path(path: str, *,
                             base: str | None = None,
                             token: str | None = None,
                             uid: str | None = None,
                             request: Callable[..., Any] = curl
) -> dict[str, Any] | None:
    """Read one existing Movie/Episode DTO by its exact library path.

    No match means the path is not indexed and is safe for the ordinary
    incoming-file mover. Multiple matches or an unavailable API fail closed.
    """
    env = load_env()
    token = token or env.get("JELLYFIN_TOKEN", "")
    base = base or resolve_base_url(env, token, request=request)
    if not base or not token:
        raise LibraryPathUpdateError("Jellyfin library credentials are unavailable")
    uid = uid or first_user_id(base, token, request=request)
    if not uid:
        raise LibraryPathUpdateError("could not resolve a Jellyfin library user")

    matches: list[dict[str, Any]] = []
    start = 0
    expected_total: int | None = None
    while True:
        url = (f"{base.rstrip('/')}/Users/{uid}/Items"
               "?Recursive=true&IncludeItemTypes=Movie,Episode&fields=Path"
               "&EnableImageTypes=Primary&Limit=500"
               f"&StartIndex={start}")
        response = request("GET", url, token)
        if not response:
            raise LibraryPathUpdateError(
                "could not enumerate the current Jellyfin library items")
        try:
            page = json.loads(response)
            rows = page["Items"]
            total = int(page["TotalRecordCount"])
        except (KeyError, TypeError, ValueError) as exc:
            raise LibraryPathUpdateError(
                "Jellyfin returned an invalid library item page") from exc
        if not isinstance(rows, list):
            raise LibraryPathUpdateError("Jellyfin item page has no item list")
        if total < 0 or (expected_total is not None and total != expected_total):
            raise LibraryPathUpdateError(
                "Jellyfin item pages report inconsistent item totals")
        expected_total = total
        if start + len(rows) > total:
            raise LibraryPathUpdateError(
                "Jellyfin item page contains more rows than its reported total")
        for item in rows:
            if (not isinstance(item, dict)
                    or not isinstance(item.get("Id"), str) or not item["Id"]
                    or not isinstance(item.get("Path"), str) or not item["Path"]):
                raise LibraryPathUpdateError(
                    "Jellyfin item page contains an invalid item row")
            if item["Path"] == path:
                matches.append(item)
        start += len(rows)
        if start >= total:
            break
        if not rows:
            raise LibraryPathUpdateError(
                "Jellyfin returned an empty page before the reported total")
    if len(matches) > 1:
        raise LibraryPathUpdateError(
            f"expected at most one library item at {path}, found {len(matches)}")
    if not matches:
        return None
    item_id = matches[0].get("Id")
    if not isinstance(item_id, str) or not item_id:
        raise LibraryPathUpdateError(f"library item at {path} has no id")

    item_url = f"{base.rstrip('/')}/Users/{uid}/Items/{item_id}"
    current = request("GET", item_url, token)
    if not current:
        raise LibraryPathUpdateError(f"could not read library item {item_id}")
    try:
        dto = json.loads(current)
    except (TypeError, ValueError) as exc:
        raise LibraryPathUpdateError(
            f"library item {item_id} returned invalid JSON") from exc
    if (not isinstance(dto, dict) or dto.get("Id") != item_id
            or dto.get("Path") != path):
        raise LibraryPathUpdateError(
            f"library item {item_id} no longer identifies {path}")
    return dto


def update_library_item_path(item: dict[str, Any], old_path: str, new_path: str, *,
                             base: str | None = None,
                             token: str | None = None,
                             uid: str | None = None,
                             request: Callable[..., Any] = curl
                             ) -> tuple[str, bool]:
    """Retain a Movie/Episode record after moving its file to ``new_path``.

    The normal Jellyfin item update API receives the existing item DTO with
    only its Path changed. After that durable metadata update, the new path is
    announced through ``/Library/Media/Updated`` so the ordinary library
    watcher refreshes it. Returns the item id and whether the notification
    succeeded; a later full scan remains the fallback for a missed notice.
    """
    env = load_env()
    token = token or env.get("JELLYFIN_TOKEN", "")
    base = base or resolve_base_url(env, token, request=request)
    if not base or not token:
        raise LibraryPathUpdateError("Jellyfin library credentials are unavailable")
    item_id = item.get("Id")
    dto_old_path = item.get("Path")
    if (not isinstance(item_id, str) or not item_id
            or not isinstance(dto_old_path, str) or dto_old_path != old_path):
        raise LibraryPathUpdateError(
            "library item DTO id or old path does not match the requested update")
    dto = dict(item)
    dto["Path"] = new_path
    updated = request(
        "POST", f"{base.rstrip('/')}/Items/{item_id}", token,
        json.dumps(dto).encode("utf-8"))
    if updated is None:
        raise LibraryPathUpdateError(f"Jellyfin refused the path update for {item_id}")

    uid = uid or first_user_id(base, token, request=request)
    if not uid:
        raise LibraryPathUpdateError("could not resolve a Jellyfin library user")
    verified = request(
        "GET", f"{base.rstrip('/')}/Items/{item_id}?userId={uid}", token)
    if not verified:
        raise LibraryPathUpdateError(
            f"could not verify the path update for library item {item_id}")
    try:
        verified_dto = json.loads(verified)
    except (TypeError, ValueError) as exc:
        raise LibraryPathUpdateError(
            f"library item {item_id} returned invalid verification JSON") from exc
    if (not isinstance(verified_dto, dict)
            or verified_dto.get("Id") != item_id
            or verified_dto.get("Path") != new_path):
        raise LibraryPathUpdateError(
            f"library item {item_id} did not retain its id at {new_path}")

    notice = json.dumps({
        "Updates": [{"Path": new_path, "UpdateType": "Created"}],
    }).encode("utf-8")
    notified = request(
        "POST", f"{base.rstrip('/')}/Library/Media/Updated", token, notice) is not None
    return item_id, notified


def _is_session_actively_playing(
        session: dict[str, Any],
        now: datetime.datetime,
        stale_after_seconds: int) -> bool:
    """Classify one session conservatively for disruptive-work gates."""
    if not session.get("NowPlayingItem"):
        return False
    if (session.get("PlayState") or {}).get("IsPaused", False):
        return False
    checkin = _parse_jellyfin_time(session.get("LastPlaybackCheckIn"))
    # An unparseable or absent check-in is treated as live: this filter exists
    # to release ghosts, never to talk itself into starting disruptive work.
    return checkin is None or (
        now - checkin).total_seconds() <= stale_after_seconds


def active_playback_sessions(
    base: str,
    token: str,
    stale_after_seconds: int = 180,
    *,
    request: Callable[..., Any] = curl,
) -> Optional[list[dict[str, Any]]]:
    """Return the sessions someone is actually watching, or None if unknown.

    None and [] mean different things and callers must not conflate them: []
    is "nobody is watching, it is safe to start heavy work", while None is
    "the server did not answer, so we do not know". A caller that treats an
    unreachable server as an idle one will start a two-hour re-encode on top
    of somebody's film.

    Three things must hold for a session to count as being watched: it has a
    NowPlayingItem, it is not paused, and it has checked in recently.

    That last condition is what makes this usable unattended. A browser tab
    that goes away without stopping playback leaves a session behind that
    still reports NowPlayingItem and IsPaused=false forever, and its
    LastActivityDate keeps updating, so only the playback check-in reveals it.
    Observed 2026-09-03 right after a promotion: a session left by the e2e
    browser reported "playing Minority Report" with LastActivityDate equal to
    now and LastPlaybackCheckIn 8 minutes stale. Without this window the
    nightly conversion would have waited on that ghost until its deadline and
    converted nothing, every night, silently.

    Clients check in every few seconds while playing, so a three-minute window
    is far outside normal jitter while still releasing a dead session quickly.

    :param base: Base URL from resolve_base_url.
    :param token: Jellyfin API token.
    :param stale_after_seconds: Ignore sessions whose last playback check-in
        is older than this.
    :param request: The HTTP transport, for tests. Same call contract as curl.
    :returns: Sessions being watched, [] when idle, or None when unknown.
    """
    out = request("GET", f"{base}/Sessions", token)
    if not out:
        return None
    try:
        sessions = json.loads(out)
    except (ValueError, TypeError):
        return None
    if not isinstance(sessions, list):
        return None

    now = datetime.datetime.now(datetime.timezone.utc)  # allow-direct-time: comparing against a server-supplied timestamp to judge staleness; there is no clock adapter in tools/lib and injecting one here would not make this any more testable
    return [
        session for session in sessions
        if _is_session_actively_playing(session, now, stale_after_seconds)
    ]


def _parse_jellyfin_time(value: Any) -> Optional[datetime.datetime]:
    """Parse a Jellyfin ISO-8601 UTC timestamp, or None when unusable.

    Jellyfin emits more fractional-second digits than datetime accepts and
    marks UTC with 'Z', neither of which fromisoformat handles on every
    supported Python, so both are normalized before parsing.
    """
    if not isinstance(value, str) or not value:
        return None
    text = value.rstrip("Z")
    if "." in text:
        head, _, frac = text.partition(".")
        text = f"{head}.{frac[:6]}"
    try:
        parsed = datetime.datetime.fromisoformat(text)
    except ValueError:
        return None
    return parsed.replace(tzinfo=datetime.timezone.utc)


def first_user_id(base: str, token: str, *,
                  request: Callable[..., Any] = curl) -> Optional[str]:
    """Return any user id (used only to scope library item queries)."""
    out = request("GET", f"{base}/Users", token)
    if not out:
        return None
    users = json.loads(out)
    return users[0]["Id"] if users else None


ScheduledTaskRun = _scheduled.ScheduledTaskRun
POST_CONVERSION_ANALYSIS_KEYS = _scheduled.POST_CONVERSION_ANALYSIS_KEYS
DEFAULT_STALL_SECONDS = _scheduled.DEFAULT_STALL_SECONDS
ABSOLUTE_TASK_CAP_SECONDS = _scheduled.ABSOLUTE_TASK_CAP_SECONDS
_scheduled_task = _scheduled._scheduled_task
_last_end_time = _scheduled._last_end_time
_wait_for_scheduled_task = _scheduled._wait_for_scheduled_task
_run_scheduled_task = _scheduled._run_scheduled_task
_analysis_task_result = _scheduled._analysis_task_result


def run_scheduled_task(base: str, token: str, key: str, *,
                       request: Callable[..., Any] = curl,
                       stall_s: int = DEFAULT_STALL_SECONDS, poll_s: int = 6,
                       clock: Callable[[], float] = time.monotonic,
                       overall_deadline: Optional[float] = None,
                       ) -> ScheduledTaskRun:
    """Trigger a scheduled task by stable key and wait for completion.

    ``overall_deadline`` is the caller's monotonic tail budget; see
    jellyfin_scheduled_tasks._wait_for_scheduled_task."""
    return _scheduled.run_scheduled_task(
        base, token, key, request=request, stall_s=stall_s,
        poll_s=poll_s, clock=clock, overall_deadline=overall_deadline)


def run_library_scan(base: str, token: str,
                     stall_s: int = DEFAULT_STALL_SECONDS,
                     poll_s: int = 6, *,
                     request: Callable[..., Any] = curl,
                     clock: Callable[[], float] = time.monotonic,
                     overall_deadline: Optional[float] = None,
                     ) -> ScheduledTaskRun:
    """Trigger the library scan and wait for proven completion.

    ``overall_deadline`` is the caller's monotonic tail budget; see
    jellyfin_scheduled_tasks._wait_for_scheduled_task."""
    return _scheduled.run_library_scan(
        base, token, request=request, stall_s=stall_s,
        poll_s=poll_s, clock=clock, overall_deadline=overall_deadline)


def _analysis_playback_stop_reason(
        base: str, token: str, stale_after_seconds: int, *,
        request: Callable[..., Any]) -> Optional[str]:
    """Return why analysis must stop, or None only for confirmed idleness."""
    return _scheduled._analysis_playback_stop_reason(
        base, token, stale_after_seconds, request=request,
        active_sessions=active_playback_sessions)


def run_post_conversion_analysis(base: str, token: str, *,
                                 request: Callable[..., Any] = curl,
                                 stall_s: int = DEFAULT_STALL_SECONDS,
                                 poll_s: int = 6,
                                 stale_after_seconds: int = 180,
                                 clock: Callable[[], float] = time.monotonic,
                                 overall_deadline: Optional[float] = None,
                                 ) -> dict[str, str]:
    """Run post-conversion readiness tasks, gated by active playback.

    ``overall_deadline`` is the caller's monotonic tail budget; see
    jellyfin_scheduled_tasks.run_post_conversion_analysis."""
    return _scheduled.run_post_conversion_analysis(
        base, token, request=request,
        active_sessions=active_playback_sessions,
        stall_s=stall_s, poll_s=poll_s,
        stale_after_seconds=stale_after_seconds, clock=clock,
        overall_deadline=overall_deadline)
