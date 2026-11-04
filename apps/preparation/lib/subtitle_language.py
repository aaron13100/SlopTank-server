#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Pure text classifier: does an SRT subtitle's text read as English?

Used by tools/lib/subtitle_pipeline.py's untagged-embedded-English extraction
path (t_pitt-untagged-english, 2026-09-23): some rips (the Pitt S01 720p
AMZN "MIKE" release) ship their only English subtitle track with no
language tag at all (ffprobe reports "" or "und"), so the tagged eng/en
lookup never finds it. Before publishing an untagged candidate track as
`{stem}.en.srt`, its extracted text is run through :func:`looks_english` to
confirm it actually IS English and not, say, a French or Japanese track
that also happened to ship untagged.

No I/O here: this module only classifies text already in memory.
"""

from __future__ import annotations

import re

# SRT cue-index lines ("7" alone on its own line) and cue timestamp lines
# ("00:01:39,351 --> 00:01:42,145", optionally with trailing positioning
# args) are structural, not spoken text, and are stripped before tokenizing
# so their digits/arrows never influence the token stream.
_INDEX_LINE_RE = re.compile(r"^\s*\d+\s*$", re.MULTILINE)
_TIMESTAMP_LINE_RE = re.compile(
    r"^\s*\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,.]\d{3}.*$",
    re.MULTILINE,
)
# ASS/SSA-style override blocks ("{\an8}", "{\pos(100,200)}") occasionally
# leak into subrip/webvtt tracks; their bracketed contents are markup, not
# words, and must be dropped whole rather than tokenized -- "{\an8}" would
# otherwise leave a spurious "an" token (itself one of the function words
# below) once digits are stripped by the tokenizer's own letters-only regex.
_OVERRIDE_TAG_RE = re.compile(r"\{\\[^}]*\}")
# HTML-style markup tags ("<i>", "</i>", '<font color="#ffffff">') are
# likewise structural, not text.
_MARKUP_TAG_RE = re.compile(r"<[^>]*>")
# Tokens are maximal runs of lowercase letters and apostrophes (so "it's",
# "don't" survive as single tokens); everything else -- punctuation, digits,
# accented characters -- is a token boundary.
_TOKEN_RE = re.compile(r"[a-z']+")

#: The 50 most common English function words. Real English dialogue -- in
#: any register, on any subject -- is dense with these; a non-English
#: track's letter-only tokens collide with this set only by rare accident
#: (a name, a loanword, a French "il" that happens not to be in this list).
FUNCTION_WORDS: frozenset[str] = frozenset({
    "the", "a", "an", "and", "of", "to", "in", "is", "it", "you", "i",
    "that", "for", "on", "with", "was", "he", "she", "we", "they", "be",
    "are", "this", "have", "not", "but", "what", "all", "do", "at", "my",
    "your", "me", "so", "no", "just", "can", "if", "get", "know", "there",
    "from", "go", "like", "or", "one", "out", "up", "about", "him", "her",
})

#: Minimum token count before a verdict is trusted at all. Below this, a
#: handful of names or numbers could produce a spuriously high (or low)
#: function-word ratio by chance; the brief's own floor.
MIN_TOKENS = 200

#: Minimum fraction of tokens that must be function words for the text to
#: read as English, once MIN_TOKENS is met.
MIN_FUNCTION_WORD_RATIO = 0.30


def tokenize(srt_text: str) -> list[str]:
    """Lowercased ``[a-z']+`` tokens from raw SRT file contents.

    Cue-index lines, cue-timestamp lines, ``{\\...}`` override tags and
    ``<...>`` markup tags are stripped first (see the module-level regexes)
    so none of their structural characters leak into the token stream.

    :param srt_text: Raw contents of an .srt file (or any subtitle text in
        the same cue-based shape).
    :returns: Tokens in reading order, lowercased.
    """
    text = _INDEX_LINE_RE.sub(" ", srt_text)
    text = _TIMESTAMP_LINE_RE.sub(" ", text)
    text = _OVERRIDE_TAG_RE.sub(" ", text)
    text = _MARKUP_TAG_RE.sub(" ", text)
    return _TOKEN_RE.findall(text.lower())


def function_word_ratio(tokens: list[str]) -> float:
    """Fraction of `tokens` present in :data:`FUNCTION_WORDS`.

    :param tokens: Tokens as returned by :func:`tokenize`.
    :returns: 0.0 for an empty token list (never divides by zero); otherwise
        a value in [0.0, 1.0].
    """
    if not tokens:
        return 0.0
    hits = sum(1 for token in tokens if token in FUNCTION_WORDS)
    return hits / len(tokens)


def looks_english(srt_text: str) -> bool:
    """Does `srt_text` read as English?

    True iff tokenizing produces at least :data:`MIN_TOKENS` tokens AND at
    least :data:`MIN_FUNCTION_WORD_RATIO` of them are common English
    function words.

    Calibrated 2026-09-23 against two real fixtures on this library:
    - Positive: the Pitt S01E03 720p AMZN "MIKE" rip's own untagged embedded
      subrip track (English) -- measured 7274 tokens, function-word ratio
      0.408 (comfortably over both thresholds).
    - Negative control: media/TV/The Pitt/Season 01/The.Pitt.S01E01.1080p.AV1.10bit-MeGusta[EZTVx.to].fr.srt
      (French) -- measured 5556 tokens, function-word ratio 0.047 (well
      under 0.30; the handful of hits are accidental collisions -- French
      "a", "me", "la" as bare letters -- not real English content).

    :param srt_text: Raw contents of an .srt file (or any subtitle text in
        the same cue-based shape).
    :returns: True if the text should be treated as English.
    """
    tokens = tokenize(srt_text)
    return len(tokens) >= MIN_TOKENS and function_word_ratio(tokens) >= MIN_FUNCTION_WORD_RATIO
