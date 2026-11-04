# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Shared exception contracts for streaming conversion modules."""


class TranscodeFailedError(Exception):
    """An attempted conversion failed while leaving the original untouched."""
