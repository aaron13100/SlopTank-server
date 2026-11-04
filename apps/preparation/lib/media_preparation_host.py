# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Context-bound service boundary for media preparation operations."""

from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar
from pathlib import Path
from typing import Iterator, Protocol


class MediaPreparationHost(Protocol):
    """Parent-owned operations required by the media preparation pipeline."""

    def preflight(self, path: Path) -> str:
        """Authorize preparation for the currently bound media path."""

    def replace(
        self, tmp: Path, original: Path, new_path: Path | None, *, item_id: object,
    ) -> Path:
        """Install staged media through the host and return its bound path."""

    def sessions_are_idle(self) -> bool:
        """Return the parent-confirmed playback idle state."""


_preparation_host: ContextVar[MediaPreparationHost | None] = ContextVar(
    "media_preparation_host", default=None,
)


def current_preparation_host() -> MediaPreparationHost | None:
    """Return the host bound in the current context, if any."""
    return _preparation_host.get()


@contextmanager
def use_preparation_host(host: MediaPreparationHost) -> Iterator[None]:
    """Bind a host for the current context and always restore its prior value."""
    token = _preparation_host.set(host)
    try:
        yield
    finally:
        _preparation_host.reset(token)
