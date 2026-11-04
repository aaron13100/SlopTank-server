#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Compare a movie's folder-derived expected identity against Jellyfin's resolved
metadata, and pick the best RemoteSearch candidate for re-identification.

Folder name is the source of truth: the human deliberately named it
(``.../Movies/<Title (Year)>/<file>``), while the metadata provider guessed and can
match an ambiguous short title to the wrong movie (e.g. ``River (2023)`` matched to
*Mystic River (2003)*). This module holds only that comparison policy -- no I/O.
"""

from __future__ import annotations

import os
import re
from difflib import SequenceMatcher
from typing import Any, Optional

# Folder year and provider release year can legitimately differ by a year
# (festival vs wide release); only gaps beyond this count as a year mismatch.
YEAR_TOLERANCE = 1
# Normalized-title similarity below which a same-year match is still suspicious.
TITLE_MATCH_FLOOR = 0.60
# Stricter bar when the folder carries no year and title is the only signal.
TITLE_MATCH_FLOOR_NOYEAR = 0.85
# Movie posters are portrait (~0.67). A primary image wider than this is a
# video-frame screen grab (16:9 = 1.78), the Screen Grabber fallback's output.
SCREENSHOT_ASPECT_FLOOR = 1.2

_FOLDER_RE = re.compile(r"^(?P<title>.+?)\s*\((?P<year>\d{4})\)\s*$")
_ARTICLE_RE = re.compile(r"^(the|a|an)\s+")
_NONWORD_RE = re.compile(r"[^a-z0-9]+")
# "Title aka Other Title" folder names: neither TMDb nor OMDb matches the
# combined string; each segment must be searched separately.
_AKA_SPLIT_RE = re.compile(r"\s+a\.?k\.?a\.?\s+", re.IGNORECASE)
# Trailing release-edition qualifiers that break provider search when left on
# ("Fight Club 10th Anniversary Edition" -> 0 TMDb results). Stripped only as a
# *search variant*; the folder name itself stays authoritative for comparison.
_EDITION_SUFFIX_RE = re.compile(
    r"\s+(?:\d+(?:st|nd|rd|th)\s+anniversary(?:\s+edition)?"
    r"|(?:director'?s?|extended|unrated|remastered|theatrical|ultimate"
    r"|special|collector'?s?|limited|definitive|deluxe|final|restored)"
    r"\s+(?:edition|cut|version))\s*$",
    re.IGNORECASE)
# Leading broadcaster/network tags folders sometimes carry ("BBC Cunk On
# Shakespeare"): providers index the bare title, the prefixed search misses.
_BROADCASTER_PREFIX_RE = re.compile(r"^(?:bbc|itv|pbs|hbo)\s+", re.IGNORECASE)


def normalize_title(title: str) -> str:
    """Lowercase, drop a leading article, strip punctuation, collapse whitespace."""
    t = title.lower().strip()
    t = _ARTICLE_RE.sub("", t)
    t = _NONWORD_RE.sub(" ", t)
    return " ".join(t.split())


def title_ratio(a: str, b: str) -> float:
    """Similarity in [0,1] between two titles after normalization."""
    return SequenceMatcher(None, normalize_title(a), normalize_title(b)).ratio()


def title_contained(a: str, b: str) -> bool:
    """True if one normalized title is a whole substring of the other.

    Clears marketing variants like ``F1`` vs ``F1 The Movie`` where similarity ratio
    is low but the shorter title is clearly a prefix/subset of the longer one.
    """
    na, nb = normalize_title(a), normalize_title(b)
    if not na or not nb:
        return False
    return na in nb or nb in na


def title_matches(expected: str, candidates: list[str], floor: float) -> tuple[bool, float]:
    """Decide whether a folder title matches any provider-supplied title.

    Compares ``expected`` against each candidate (typically Name and OriginalTitle)
    using both similarity ratio and substring containment. Returns
    (matched, best_ratio).
    """
    best = 0.0
    matched = False
    for cand in candidates:
        if not cand:
            continue
        r = title_ratio(expected, cand)
        best = max(best, r)
        if r >= floor or title_contained(expected, cand):
            matched = True
    return matched, best


def search_title_variants(title: str) -> list[str]:
    """Ordered, deduped title variants to try against provider RemoteSearch.

    The full folder title comes first (most specific when it does match), then
    each " aka " segment, then edition-suffix-stripped forms of all of the
    above. Regression: "The Dinner Game aka Le diner de cons" and "Fight Club
    10th Anniversary Edition" both return 0 TMDb results verbatim, while their
    cleaned variants match immediately (2026-07-22 incident).
    """
    ordered = [title.strip()]
    ordered += [seg.strip() for seg in _AKA_SPLIT_RE.split(title) if seg.strip()]
    for cand in list(ordered):
        for cleaner in (_EDITION_SUFFIX_RE, _BROADCASTER_PREFIX_RE):
            stripped = cleaner.sub("", cand).strip()
            if stripped and stripped != cand:
                ordered.append(stripped)
    variants: list[str] = []
    seen: set[str] = set()
    for cand in ordered:
        key = cand.lower()
        if key not in seen:
            seen.add(key)
            variants.append(cand)
    return variants


def _identity_gap(item: dict[str, Any]) -> Optional[tuple[str, str]]:
    """Detect absent identity or degraded artwork; returns (reason, action).

    These are the failure modes the folder-comparison check is structurally
    blind to: an unidentified item's Name/Year are parsed *from* the folder, so
    they trivially agree with it (2026-07-22: Fight Club / The Dinner Game lost
    ProviderIds in a raced refresh and audited clean while their primaries were
    Screen Grabber video frames).
    """
    ids = {k.lower(): v for k, v in (item.get("ProviderIds") or {}).items() if v}
    if not ids.get("tmdb") and not ids.get("imdb"):
        return "unidentified (no provider ids)", "reidentify"
    if not (item.get("ImageTags") or {}).get("Primary"):
        return "missing primary image", "refresh_images"
    aspect = item.get("PrimaryImageAspectRatio")
    if aspect and aspect >= SCREENSHOT_ASPECT_FLOOR:
        return f"screenshot-shaped primary image (aspect {aspect:.2f})", "refresh_images"
    return None


def expected_from_path(path: str) -> tuple[str, Optional[int]]:
    """Derive (title, year) from a movie file's parent folder name.

    Movies are stored one-folder-per-title as ``.../Movies/<Title (Year)>/<file>``.
    Returns the folder title and its year, or (folder_name, None) when no year suffix.
    """
    folder = os.path.basename(os.path.dirname(path))
    m = _FOLDER_RE.match(folder)
    if m:
        return m.group("title").strip(), int(m.group("year"))
    return folder.strip(), None


def evaluate(item: dict[str, Any]) -> Optional[dict[str, Any]]:
    """Compare one item's resolved identity to its folder-derived expectation.

    Returns a mismatch record when the item looks misidentified, else None. The
    record carries a ``confidence``:

    - ``high``  -- the folder year and the resolved year disagree beyond tolerance.
                   This is the genuine misidentification mode (e.g. River 2023
                   matched to Mystic River 2003) and is the only case ``--fix``
                   will auto-repair.
    - ``low``   -- years agree (or no folder year) but no provider title (Name or
                   OriginalTitle) matches the folder. Usually a localized/alternate
                   title the heuristic could not confirm; reported for manual review,
                   never auto-applied.

    Localized and marketing titles are cleared by checking both Name and
    OriginalTitle with substring containment, so foreign-language films
    (``L'Auberge espagnole`` -> ``The Spanish Apartment``) do not flag.
    """
    path = item.get("Path") or ""
    if not path:
        return None
    exp_title, exp_year = expected_from_path(path)
    got_name = item.get("Name") or ""
    got_orig = item.get("OriginalTitle") or ""
    got_year = item.get("ProductionYear")
    floor = TITLE_MATCH_FLOOR if exp_year is not None else TITLE_MATCH_FLOOR_NOYEAR
    title_ok, best_ratio = title_matches(exp_title, [got_name, got_orig], floor)

    gap = _identity_gap(item)
    if gap is not None:
        confidence, (reason, action) = "high", gap
    elif exp_year is not None and (got_year is None or abs(int(got_year) - exp_year) > YEAR_TOLERANCE):
        confidence, reason, action = "high", "year mismatch", "reidentify"
    elif not title_ok:
        confidence, action = "low", "reidentify"
        reason = "title mismatch (same year)" if exp_year is not None else "title mismatch (no folder year)"
    else:
        return None

    return {
        "id": item.get("Id"),
        "path": path,
        "confidence": confidence,
        "action": action,
        "expected": {"title": exp_title, "year": exp_year},
        "resolved": {"name": got_name, "original": got_orig, "year": got_year,
                     "tmdb": (item.get("ProviderIds") or {}).get("Tmdb")},
        "title_ratio": round(best_ratio, 2),
        "reason": reason,
    }


def choose_candidate(results: list[dict[str, Any]], exp_title: str,
                      exp_year: Optional[int]) -> Optional[dict[str, Any]]:
    """Pick the best RemoteSearch result for re-identification, or None if low confidence.

    A candidate qualifies only when its year is within tolerance of the folder year
    (when known) and its normalized title is a close match or a whole substring of
    the folder title (TMDb returns "Fight Club" for folder "Fight Club 10th
    Anniversary Edition"; the ratio is 0.44 but containment clears it). Among
    qualifiers, the highest title similarity wins.
    """
    scored: list[tuple[float, dict[str, Any]]] = []
    for r in results:
        cand_year = r.get("ProductionYear")
        if exp_year is not None:
            if cand_year is None or abs(int(cand_year) - exp_year) > YEAR_TOLERANCE:
                continue
        cand_name = r.get("Name") or ""
        ratio = title_ratio(exp_title, cand_name)
        floor = TITLE_MATCH_FLOOR if exp_year is not None else TITLE_MATCH_FLOOR_NOYEAR
        # Containment only counts alongside a validated year: without a folder
        # year the substring check alone is too loose to apply an identity.
        if ratio < floor and not (exp_year is not None
                                  and title_contained(exp_title, cand_name)):
            continue
        scored.append((ratio, r))
    if not scored:
        return None
    scored.sort(key=lambda x: x[0], reverse=True)
    return scored[0][1]
