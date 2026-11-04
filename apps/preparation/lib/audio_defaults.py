#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Prefer an English soundtrack while keeping every selectable audio track."""

from __future__ import annotations

import json
import logging
import re
import shutil  # test seam: tests patch audio_defaults.shutil.which (this same
               # global module); require_tool resolves through it, so it stays.
from pathlib import Path

from lib.encode_cancellation import EncodeCancelled, run_cancellable_capture
from lib.media_tools import require_tool
from lib.media_probe_cache import run_cached_probe
from lib.organize_media_config import (
    BROWSER_AUDIO_CODECS,
    TV_AUDIO_CODECS,
    TV_AUDIO_SAMPLE_RATES,
    TV_MULTICHANNEL_AAC_CONTAINERS,
)

log = logging.getLogger("organize-media")

_ALTERNATE_AUDIO_RE = re.compile(
    r"\b(?:riff(?:trax)?|commentar(?:y|ies)|audio[\s_-]*description|"
    r"descriptive[\s_-]*audio|described[\s_-]*video)\b",
    re.IGNORECASE,
)
_ENGLISH_AUDIO_RE = re.compile(r"\benglish\b", re.IGNORECASE)


def _probe_audio_streams(path: Path) -> list[dict]:
    """Return audio streams in file order, including titles and dispositions.

    Returns [] both for a file that genuinely has no audio and for a probe that
    failed. Callers read [] as "no audio tracks", which is the safe direction to
    fail, so the distinction is carried in the LOG rather than in the return
    value: an unreadable file that silently skips normalization or track
    selection leaves nothing to explain why it was passed over. The legitimate
    empty case stays quiet on purpose, so a warning here always means something
    went wrong.

    A missing ffprobe binary is NOT a probe failure: it raises, because every
    probe on this host would fail the same way and [] would read as "no audio
    tracks" for the whole library.

    :param path: Media file to probe.
    :returns: Raw ffprobe audio stream dicts in file order, or [].
    :raises MediaToolMissingError: ffprobe cannot be resolved.
    """
    ffprobe = require_tool("ffprobe")
    try:
        result = run_cached_probe(
            [
                ffprobe,
                "-v",
                "error",
                "-select_streams",
                "a",
                "-show_streams",
                "-of",
                "json",
                str(path),
            ],
            # A COLD read of a large library file can itself take a long
            # time on this host (measured 2026-09-06 against real library
            # files: 29-66s for 2.8-10.5 GB files on their first touch, 0.3s
            # on every touch after); 30s silently misreported those as
            # unprobeable before this file's own reads had ever landed.
            path=path,
            timeout=300,
            runner=run_cancellable_capture,
        )
        if result.returncode != 0 or not result.stdout:
            log.warning("could not probe audio streams of %s: ffprobe rc=%s: %s",
                        path.name, result.returncode,
                        (result.stderr or "").strip() or "<no stderr>")
            return []
        return json.loads(result.stdout).get("streams", [])
    except EncodeCancelled:
        raise
    except Exception as exc:
        log.warning("could not probe audio streams of %s: %s", path.name, exc)
        return []


def _is_alternate_audio(stream: dict) -> bool:
    """True for tracks clearly labeled as commentary/riff/descriptive audio."""
    disposition = stream.get("disposition") or {}
    if disposition.get("comment") or disposition.get("visual_impaired"):
        return True
    tags = stream.get("tags") or {}
    label = " ".join(
        str(tags.get(key) or "")
        for key in ("title", "handler_name")
    )
    return bool(_ALTERNATE_AUDIO_RE.search(label))


def _is_english_audio(stream: dict) -> bool:
    """True when an audio stream is tagged or titled as English."""
    tags = stream.get("tags") or {}
    language = str(tags.get("language") or "").strip().lower()
    if language in {"en", "eng", "english"} or language.startswith(("en-", "en_")):
        return True
    label = " ".join(
        str(tags.get(key) or "")
        for key in ("title", "handler_name")
    )
    return bool(_ENGLISH_AUDIO_RE.search(label))


def _is_browser_playable_audio(stream: dict) -> bool:
    """True when this stream's own codec plays in Chrome and Firefox.

    AC3/E-AC3 are NOT here even though the pipeline copies them unchanged: a
    client that follows the default disposition still gets a server-side
    transcode if the default track is one of those, which is exactly the
    defect this function exists to fix (see D1 in c35's brief).
    """
    return str(stream.get("codec_name") or "").strip().lower() in BROWSER_AUDIO_CODECS


def _is_tv_playable_audio(stream: dict, container_suffix: str) -> bool:
    """Whether the Panasonic TX-LF37E30 USB target can play this track as-is.

    AAC or AC3 at 48 kHz, any channel count, EXCEPT that multichannel AAC
    needs a container on the TV_MULTICHANNEL_AAC_CONTAINERS list. That last
    clause is not a guess: round 1 clip 02 and round 2 clip 15 carry streams
    identical in every dimension ffprobe reports and differ only in container,
    and the MP4 one was silent while the MKV one played.

    ``container_suffix`` is required rather than defaulted precisely because
    the answer genuinely depends on it. A default would let a caller that had
    not thought about the container get a confidently wrong "playable", which
    is the direction that leaves a file silent on the set with nothing
    flagging it.

    :param stream: One raw ffprobe audio stream dict.
    :param container_suffix: The container the track will be IN when the TV
        reads it, e.g. ".mp4" or ".mkv"; for a conversion that is the TARGET
        container, not the source's.
    :returns: True only for combinations actually observed to play.
    """
    codec = str(stream.get("codec_name") or "").strip().lower()
    if codec not in TV_AUDIO_CODECS:
        return False
    try:
        rate = int(stream.get("sample_rate") or 0)
    except (TypeError, ValueError):
        return False
    if rate not in TV_AUDIO_SAMPLE_RATES:
        return False
    if codec == "aac" and int(stream.get("channels") or 0) > 2:
        return str(container_suffix or "").lower() in TV_MULTICHANNEL_AAC_CONTAINERS
    return True


def _prefer_playable_indexes(streams: list[dict], indexes: list[int],
                             container_suffix: str | None) -> list[int]:
    """Apply browser and television playability tiers when each has a match."""
    browser_indexes = [index for index in indexes
                       if _is_browser_playable_audio(streams[index])]
    preferred_indexes = browser_indexes or indexes
    if container_suffix is None:
        return preferred_indexes
    tv_indexes = [index for index in preferred_indexes
                  if _is_tv_playable_audio(streams[index], container_suffix)]
    return tv_indexes or preferred_indexes


def _resolve_default_index(streams: list[dict],
                           container_suffix: str | None = None) -> int | None:
    """Which audio stream should carry the default flag, ignoring whether the
    file already agrees. None only when there is nothing to prefer (fewer
    than two streams, or every stream is alternate audio).

    Preference order: English over non-English, browser-playable codec over
    one a browser cannot decode, TV-playable over one the television cannot,
    and among ties, whichever is already default. Each playability tier is
    skipped when NONE of the surviving candidates satisfies it (nothing to
    gain by picking among tracks that all fail it), which is also what keeps
    this backward compatible with callers that pass streams with no
    codec_name at all.

    The TV tier is what stops a browser-playable but television-silent track
    keeping the flag. AAC 5.1 in an MP4 is exactly that, and the television
    obeys the flag: without this tier the pipeline adds a stereo track the set
    then never selects, so the file is "fixed" and still silent. 35 library
    files are that case and 4 more are the 96 kHz one.

    :param container_suffix: The container the tracks will be IN, needed
        because TV playability depends on it. None means "do not consider the
        television", which is the right answer only for a caller that has no
        container to name.
    """
    if len(streams) < 2:
        return None
    normal_indexes = [
        index for index, stream in enumerate(streams)
        if not _is_alternate_audio(stream)
    ]
    if not normal_indexes:
        return None
    english_indexes = [
        index for index in normal_indexes
        if _is_english_audio(streams[index])
    ]
    preferred_indexes = english_indexes or normal_indexes
    preferred_indexes = _prefer_playable_indexes(
        streams, preferred_indexes, container_suffix)
    default_indexes = [
        index for index, stream in enumerate(streams)
        if (stream.get("disposition") or {}).get("default")
    ]
    for index in default_indexes:
        if index in preferred_indexes:
            return index
    return preferred_indexes[0]


def _identify_normal_soundtrack(streams: list[dict], *,
                                known_riff_franchise: bool = False,
                                ) -> tuple[int | None, bool]:
    """Which physical stream IS the film's normal soundtrack (content
    identity), unlike _resolve_default_index (decodability) -- design doc
    2.3/2.4. Returns (index, is_ambiguous); ambiguous means report, don't
    auto-apply. The channel-count override (2.3's amendment) fires only when
    the sole default candidate is stereo/mono AND both a higher-channel AND
    another stereo/mono candidate exist. known_riff_franchise: caller-
    confirmed allowlist entry only."""
    def ch(i: int) -> int:
        try:
            return int(streams[i].get("channels") or 0)
        except (TypeError, ValueError):
            return 0

    non_alternate = [i for i, s in enumerate(streams) if not _is_alternate_audio(s)]
    if not non_alternate:
        return None, False
    english = [i for i in non_alternate if _is_english_audio(streams[i])]
    candidates = english or non_alternate
    defaults = [i for i in candidates if (streams[i].get("disposition") or {}).get("default")]
    if len(defaults) != 1:
        return candidates[0], False
    chosen = defaults[0]
    others = [i for i in candidates if i != chosen]
    higher = [i for i in others if ch(i) > 2]
    low = [i for i in others if ch(i) <= 2]
    if not (ch(chosen) <= 2 and higher and low):
        return chosen, False
    top = max(higher, key=ch)
    tied = [i for i in higher if ch(i) == ch(top)]
    return (top, False) if known_riff_franchise and len(tied) == 1 else (chosen, True)


def _preferred_default_index(streams: list[dict],
                             container_suffix: str | None = None) -> int | None:
    """Return the preferred audio-relative default index, or None if unchanged."""
    resolved = _resolve_default_index(streams, container_suffix)
    if resolved is None:
        return None
    default_indexes = [
        index for index, stream in enumerate(streams)
        if (stream.get("disposition") or {}).get("default")
    ]
    if len(default_indexes) == 1 and default_indexes[0] == resolved:
        return None
    return resolved


def _disposition_value(stream: dict, make_default: bool) -> str:
    """Build an ffmpeg disposition value while preserving every other flag."""
    dispositions = dict(stream.get("disposition") or {})
    dispositions["default"] = int(make_default)
    enabled = [name for name, value in dispositions.items() if value]
    return "+".join(enabled) if enabled else "0"


def _build_remux_cmd(path: Path, tmp: Path, streams: list[dict],
                     preferred_index: int) -> list[str]:
    ffmpeg = require_tool("ffmpeg")
    cmd = [
        ffmpeg,
        "-nostdin",
        "-y",
        "-i",
        str(path),
        "-map",
        "0",
        "-map_metadata",
        "0",
        "-map_chapters",
        "0",
        "-c",
        "copy",
    ]
    for index, stream in enumerate(streams):
        cmd += [
            f"-disposition:a:{index}",
            _disposition_value(stream, index == preferred_index),
        ]
    cmd.append(str(tmp))
    return cmd

