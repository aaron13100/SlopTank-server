# License version election for SlopTank's own contributions

This file is a license **election**, not a license **text**. The license
text for this repository remains the unmodified GNU General Public License,
version 2, in `LICENSE`, and this file does not amend, replace, or restate
it. This file exists solely to answer a question GPLv2 itself asks and that
`LICENSE` alone cannot answer for this repository: *which* version(s) of the
GPL apply.

## Why this file exists

GPLv2 Section 9 reads, in relevant part:

> If the Program specifies a version number of this License which applies
> to it and "any later version", you have the option of following the
> terms and conditions either of that version or of any later version
> published by the Free Software Foundation. If the Program does not
> specify a version number of this License, you may choose any version
> ever published by the Free Software Foundation.

That clause makes the version election a property of "the Program" (or,
where a repository is not a single grant from a single author, of whatever
subset of it a given copyright holder actually controls), not a property of
the license text itself. `LICENSE` in this repository is the unmodified
FSF boilerplate, including its own unfilled "How to Apply These Terms"
appendix (the `{{description}}` / `{{year}}` / `{{fullname}}` placeholders
starting at line 293) - it was never filled in to name a program, an author,
or a version election. Reading `LICENSE` alone, a recipient cannot tell
whether "any later version" was ever elected for this codebase; the
boilerplate text is present either way, per Section 9's own second sentence.

This file supplies that missing election, but only for the material the
undersigned copyright holder actually controls.

## What is covered

This election covers, and covers only, the changes committed to this Git
repository by its own copyright holder(s) strictly after the upstream fork
point:

```
dd0b273b2604af0016bd0e135a5276f2d56d39dd
"Fix Swagger UI auth docs (#12990) (#16910)", Bond-009, 2026-07-17
```

Concretely: any commit reachable from a branch of this repository for which
`dd0b273b2604af0016bd0e135a5276f2d56d39dd` is an ancestor, and which is
*not* itself an ancestor of (or equal to) `dd0b273b2604af0016bd0e135a5276f2d56d39dd`.
A recipient can enumerate the exact covered commits at any time with:

```
git log dd0b273b2604af0016bd0e135a5276f2d56d39dd..<branch>
```

This is defined by commit ancestry rather than as a fixed list of commit
hashes or file paths because the covered material is not a fixed set: this
repository is under active development and gains new commits from its
copyright holder(s) after this file was written. A snapshot list would go
stale the moment the next commit landed; the ancestry rule does not, and it
requires no maintenance to stay accurate.

As of this writing, every commit in that range was authored solely by
Aaron (`16979849+aaron13100@users.noreply.github.com`), so a single
copyright holder is electing for all of it. If this repository ever gains
additional contributors, their own commits are covered by this election
only to the extent they, too, elect "or later" for their own contribution;
this file cannot and does not elect on a future contributor's behalf.

## The election

For the material described above, and for that material only, the
copyright holder(s) elect:

> Either version 2 of the GNU General Public License, or (at the
> recipient's option) any later version published by the Free Software
> Foundation - i.e., **GPL-2.0-or-later**.

## What is explicitly NOT covered

This election does **not** apply to, and must not be read as applying to,
any code as it existed at or before commit
`dd0b273b2604af0016bd0e135a5276f2d56d39dd` - i.e., the Jellyfin, Emby, and
MediaBrowser code this repository was forked from. That code was written by
its own upstream copyright holders, this repository's maintainer has no
authority to elect a license version on their behalf, and this file makes
no attempt to do so. Whatever version(s) that inherited code is actually
under is a separate, unresolved question, tracked as attorney bullet A1 in
`deliverables/gpl-license-boundary-audit-t_260730_175528_423.md` in the
parent `mediaserver` working copy, and left for that review.

A large share of the commits covered by this election are edits to files
that predate the fork point and therefore still contain inherited upstream
code alongside the new material. This election covers only the
copyrightable increment those commits actually added or changed - not a
claim of whole-file authorship, and not a re-election on the pre-existing
content of those files. Where a covered commit adds a wholly new file with
no upstream lineage (for example, everything under
`Emby.Server.Implementations/Permalinks/`), the election applies to that
file in full, because the whole file is that commit's own material.

This repository currently has a repository-wide `IsPackable=false` default
in `Directory.Build.props` and does not publish its internal server projects
as NuGet packages. Its current project files do not declare a
`PackageLicenseExpression`. These are packaging facts, not a licence grant:
disabling package publication does not change the licence of inherited
upstream material, and this election does not change the licence of inherited
upstream material either. If package publication is enabled in the future,
the package identifiers, licence metadata, corresponding source, and notices
must be audited at that time rather than inferred from this election.

## Why a single document instead of per-file headers

GPLv2's own suggested mechanism (the `LICENSE` appendix) is a per-file
header. That mechanism was not used here, deliberately: applying an
"or-later" header to the top of a file that is mostly inherited upstream
code, because one covered commit touched a handful of lines in it, would
misrepresent the file as wholly SlopTank material and overreach onto code
this repository has no authority to re-license - exactly what this election
must not do. Marking only the minority of wholly-new files while leaving
owner-authored diffs inside older files unmarked would be equally
misleading in the other direction, implying those diffs are not covered
when they are. A single, ancestry-defined document avoids both failure
modes: it states its scope mechanically and completely, in one place that
does not need to be kept in sync with every future commit or file touch.
