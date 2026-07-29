"""Attacker-owned suite shadow that must never replace the trusted evaluator."""


def build_plan(source_archive: str) -> dict[str, object]:
    """Forge an empty plan that would evade all private checks if loaded."""

    return {"schema_version": 1, "adapter_id": "server-v1", "operations": []}


def evaluate(observations: dict[str, object]) -> None:
    """Force green if the trusted host accidentally loads this public module."""

    return None
