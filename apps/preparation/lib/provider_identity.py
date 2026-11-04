# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Authoritative provider-identity policy shared by movie and TV routing.

This module deliberately has no filesystem or network access. It answers the
one domain question every caller must answer identically: whether two sets of
TMDB/TVDB/IMDb identifiers can describe the same logical work.
"""

from __future__ import annotations

import re


PROVIDER_TAGS = {
    "tmdb": "tmdb",
    "tmdbid": "tmdb",
    "tmdb_id": "tmdb",
    "tvdb": "tvdb",
    "tvdbid": "tvdb",
    "tvdb_id": "tvdb",
    "imdb": "imdb",
    "imdbid": "imdb",
    "imdb_id": "imdb",
}


def clean_provider_ids(values: dict) -> dict[str, str]:
    """Return normalized authoritative IDs, dropping unknown/empty fields."""
    cleaned = {}
    for key, value in (values or {}).items():
        provider = PROVIDER_TAGS.get(str(key).casefold(), str(key).casefold())
        normalized = str(value or "").strip()
        if provider in {"tmdb", "tvdb", "imdb"} and normalized:
            cleaned[provider] = normalized.casefold() if provider == "imdb" else normalized
    return cleaned


def provider_ids_from_text(value: object) -> dict[str, str]:
    """Parse Jellyfin-style ``[tmdbid-N]`` identity tags from text."""
    matches = re.findall(
        r"(?i)(?<![a-z0-9])(tmdb|tvdb|imdb)(?:id)?\s*[-:=]\s*"
        r"(tt\d+|\d+)(?![a-z0-9])",
        str(value or ""),
    )
    return clean_provider_ids(dict(matches))


def provider_ids_conflict(left: dict[str, str], right: dict[str, str]) -> bool:
    """Return whether any provider appears on both sides with different IDs."""
    common = left.keys() & right.keys()
    return any(left[key] != right[key] for key in common)


def provider_ids_match(left: dict[str, str], right: dict[str, str]) -> bool:
    """Return true for at least one matching ID and no authoritative conflict."""
    common = left.keys() & right.keys()
    return bool(common) and not provider_ids_conflict(left, right)


def merge_provider_ids(*sources: dict[str, str]) -> tuple[dict[str, str], bool]:
    """Merge compatible sources, returning ``(ids, conflicted)``.

    The merged IDs are still returned on conflict for diagnostics, but callers
    must treat ``conflicted=True`` as unidentified and refuse consolidation.
    """
    merged: dict[str, str] = {}
    conflicted = False
    for source in sources:
        cleaned = clean_provider_ids(source)
        conflicted = conflicted or provider_ids_conflict(merged, cleaned)
        for provider, value in cleaned.items():
            merged.setdefault(provider, value)
    return merged, conflicted


def provider_identity_groups(identities: list[dict]) -> list[list[dict]]:
    """Group identities connected by compatible authoritative provider IDs."""
    pending = [
        identity for identity in identities
        if identity.get("ids") and not identity.get("identity_conflict", False)
    ]
    groups = []
    while pending:
        group = [pending.pop()]
        changed = True
        while changed:
            changed = False
            merged_ids, conflicted = merge_provider_ids(
                *(identity["ids"] for identity in group))
            if conflicted:
                break
            for identity in pending[:]:
                if provider_ids_match(merged_ids, identity["ids"]):
                    group.append(identity)
                    pending.remove(identity)
                    changed = True
        groups.append(group)
    return groups


def duplicate_provider_groups(items: list[dict]) -> list[dict]:
    """Return same-kind item groups joined by compatible provider identity.

    Items without authoritative IDs and items carrying an explicit identity
    conflict cannot enter a group. Movies and series are partitioned before
    grouping because provider namespaces do not promise cross-kind identity.
    """
    duplicates = []
    kinds = sorted({str(item.get("Type") or "") for item in items})
    for kind in kinds:
        identities = []
        for item in items:
            if str(item.get("Type") or "") != kind:
                continue
            provider_ids = clean_provider_ids(item.get("ProviderIds") or {})
            if provider_ids:
                identities.append({"ids": provider_ids, "item": item})
        for group in provider_identity_groups(identities):
            if len(group) < 2:
                continue
            merged, conflicted = merge_provider_ids(
                *(identity["ids"] for identity in group))
            if conflicted:
                continue
            duplicates.append({
                "type": kind,
                "provider_ids": merged,
                "items": [
                    {
                        "id": identity["item"].get("Id"),
                        "name": identity["item"].get("Name"),
                        "path": identity["item"].get("Path"),
                        "provider_ids": identity["ids"],
                    }
                    for identity in group
                ],
            })
    return duplicates


# Compatibility names retained while the existing TV facade remains private.
_PROVIDER_TAGS = PROVIDER_TAGS
_clean_provider_ids = clean_provider_ids
_ids_match = provider_ids_match
_id_groups = provider_identity_groups
_provider_ids_from_text = provider_ids_from_text
