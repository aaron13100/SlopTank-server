<!-- SlopTank modification notice: added or changed by SlopTank on 2026-09-20. -->

# Git hooks in this fork

`pre-commit` here enforces the GPLv2 per-file modification notices via
`../tools/check-gpl-modified-file-notices.py` in the parent mediaserver
checkout. It is one gate among several: the rest (no-skipped-tests,
no-silent-catches, no-em-dashes, no-missing-timestamps,
block-mechanical-splits) live in the machine-wide chain at
`~/.config/git/hooks`.

Wire it up in a fresh clone with a symlink, and leave `core.hooksPath`
UNSET:

    ln -sf ../../.githooks/pre-commit .git/hooks/pre-commit

That ordering is deliberate. The machine-wide `pre-commit` runs
`$(git rev-parse --git-common-dir)/hooks/pre-commit` first and only then
its own dispatchers, so a symlink gets both gates. Pointing
`core.hooksPath` at this directory instead REPLACES the machine-wide hook
rather than adding to it, which silently drops every other gate; chaining
back to it by hand from a script here does restore them, but it shifts the
process depth the chain uses to recover the `git commit` argv, so
`no-bulk-commit.sh` then refuses every commit as unscoped. Both failure
modes were observed on 2026-09-20.
