# SlopTank modification notice: added or changed by SlopTank on 2026-11-05.
"""Shared notice generator's typed failure, without deployment machinery."""

class DeploymentError(Exception):
    """A release cannot be assembled safely."""
