#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Single source of truth for ffmpeg/ffprobe resolution (c1).

Every media stage used to resolve its tool with a bare ``shutil.which`` and
treat None as "nothing to do", so a broken PATH turned a run into a silent
no-op that still exited 0. The cron-PATH incident (wrappers never exported a
PATH containing /usr/local/bin; fixed at the wrapper layer by 2b7bb3f) is one
instance; this module is the Python-layer contract that makes the same fault
loud from any entry point: a missing tool raises :class:`MediaToolMissingError`
instead of returning unchanged input.
"""

from __future__ import annotations

import os
import shutil

from lib.organize_media_config import media_tool_override, tool_search_path


class MediaToolMissingError(RuntimeError):
    """Typed failure for a media tool (ffmpeg/ffprobe) that cannot be resolved.

    Carries a stable ``code`` so callers can branch, the missing ``tool`` name,
    the ``search_path`` that was searched, a human ``message``, an actionable
    ``hint``, and the underlying ``cause`` when one exists.
    """

    def __init__(
        self,
        tool: str,
        message: str,
        *,
        search_path: str = "",
        hint: str | None = None,
        cause: BaseException | None = None,
    ) -> None:
        super().__init__(message)
        self.code = "media_tool_missing"
        self.tool = tool
        self.message = message
        self.search_path = search_path
        self.hint = hint
        self.cause = cause

    def __str__(self) -> str:
        parts = [f"[{self.code}] {self.message}"]
        if self.hint:
            parts.append(f"hint: {self.hint}")
        if self.cause is not None:
            parts.append(f"cause: {self.cause}")
        return " | ".join(parts)


def require_tool(name: str) -> str:
    """Resolve a media tool to an executable path, or raise.

    Resolution order: when the ``name.upper()`` environment override (FFMPEG /
    FFPROBE) is set it must be an executable regular file and is returned;
    a set-but-broken override raises without falling back to PATH, because a
    broken override is an operator error that a silent fallback would mask.
    With no override, ``shutil.which`` resolves from PATH (called as the
    module attribute at call time, so existing tests patching
    ``<module>.shutil.which`` keep working: that is this same global module).

    There is deliberately no hardcoded fallback list: a silent fallback would
    hide an environment divergence (the organizer and the backfill running
    different ffmpeg builds). Loud failure plus the wrapper preflight is the
    contract.

    :param name: Tool to resolve ("ffmpeg" or "ffprobe").
    :returns: Absolute path to the executable tool.
    :raises MediaToolMissingError: If the tool cannot be resolved.
    """
    override = media_tool_override(name)
    if override is not None:
        if os.path.isfile(override) and os.access(override, os.X_OK):
            return override
        raise MediaToolMissingError(
            name,
            f"{name} override {override!r} is not an executable file",
            search_path=override,
            hint=(f"set {name.upper()} to an executable file, or unset it "
                  f"to resolve {name} from PATH"),
        )
    found = shutil.which(name)
    if found is not None:
        return found
    raise MediaToolMissingError(
        name,
        f"{name} not found on PATH",
        search_path=tool_search_path(),
        hint=("export PATH with /usr/local/bin in the calling wrapper "
              "(see tests/test_wrapper_media_tool_path.py), or set "
              "FFMPEG/FFPROBE to an absolute path"),
    )


def require_media_tools() -> dict[str, str]:
    """Resolve both ffmpeg and ffprobe, or raise on the first missing one.

    :returns: Mapping of tool name to executable path.
    :raises MediaToolMissingError: If either tool cannot be resolved.
    """
    return {
        "ffmpeg": require_tool("ffmpeg"),
        "ffprobe": require_tool("ffprobe"),
    }


def media_tool_missing_in_chain(exc: BaseException) -> MediaToolMissingError | None:
    """Find a MediaToolMissingError anywhere in an exception chain (cycle-safe).

    Any layer that wraps the conversion's exception must not turn a host
    condition (no ffmpeg/ffprobe on this machine) into a per-file failure
    strike. Shaped like backfill_supervision.cancellation_in_chain.

    :param exc: The outermost exception to walk from.
    :returns: The first MediaToolMissingError found, or None.
    """
    pending: list[BaseException] = [exc]
    visited: set[int] = set()
    while pending:
        current = pending.pop()
        if id(current) in visited:
            continue
        visited.add(id(current))
        if isinstance(current, MediaToolMissingError):
            return current
        if current.__cause__ is not None:
            pending.append(current.__cause__)
        if current.__context__ is not None:
            pending.append(current.__context__)
    return None
