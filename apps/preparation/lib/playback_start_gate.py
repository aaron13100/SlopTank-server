# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Start-of-run gate: refuse to begin a heavy media rewrite while someone watches.

The conservative "unknown means busy" Jellyfin /Sessions check shared by the
in-place media-rewrite tools (fix-tv-audio-catchup.py, realign-mp4-start.py).
It lives here, in lib/, so those entry-point scripts share it by importing a
library module rather than one script loading the other by path.

A re-encode saturates this shared 2-core host's disk and both cores, so
starting one during playback is the one thing these batches must never do.
Between-item yielding for a long run is lib/playback_yield.py's job; this is
the single check made before a rewrite starts.
"""

from __future__ import annotations

import sys

from lib import jellyfin_client
from lib import media_preparation_host


def sessions_are_idle(*, env: dict | None = None, request=None) -> bool:
    """True only when Jellyfin confirms nobody is actively watching.

    Conservative like backfill-streaming.py's _wait_while_watching: a missing
    token, an unreachable server, or an undecodable response all count as
    "cannot confirm idle" and refuse the run, never as "assume idle" -- a
    re-encode saturates this shared 2-core host's disk and both cores, so
    starting one during playback is the one thing this batch must never do.

    :param env: Jellyfin connection settings; defaults to tools/jellyfin.env.
    :param request: Transport override with jellyfin_client.curl's call
        contract, for driving this through a scripted server in tests.
    :returns: True when the server answered and no session is playing.
    """
    host = media_preparation_host.current_preparation_host()
    if host is not None:
        return host.sessions_are_idle()
    kwargs = {"request": request} if request is not None else {}
    env = env if env is not None else jellyfin_client.load_env()
    token = env.get("JELLYFIN_TOKEN", "")
    if not token:
        print("no JELLYFIN_TOKEN in tools/jellyfin.env; refusing to run", file=sys.stderr)
        return False
    base = jellyfin_client.resolve_base_url(env, token, **kwargs)
    if not base:
        print("Jellyfin unreachable; refusing to run", file=sys.stderr)
        return False
    playing = jellyfin_client.active_playback_sessions(base, token, **kwargs)
    if playing is None:
        print("could not determine playback state; refusing to run", file=sys.stderr)
        return False
    if playing:
        who = ", ".join(
            f"{s.get('UserName') or '?'}: {(s.get('NowPlayingItem') or {}).get('Name') or '?'}"
            for s in playing)
        print(f"someone is watching ({who}); skipping this run", file=sys.stderr)
        return False
    return True
