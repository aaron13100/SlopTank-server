<!-- SlopTank modification notice: added or changed by SlopTank on 2026-09-07, 2026-09-09. -->
# SlopTank server distribution source and licence status

Every externally delivered server unit must be a completed delivery directory
containing the signed `SlopTank Server.app` and `SOURCE-MANIFEST.json`. The
manifest names an immutable source archive, its SHA-256 digest, the exact Git
commit, dependency/build inputs and toolchain, the scoped licence position,
and a digest covering the signed app and every other delivery byte. The
archive must be reachable by an unauthenticated recipient before delivery.

The source archive includes this file, `release-source.json`, the central
package pins, SDK selection, build/install scripts, and the rest of the exact
commit. Archives and their URLs are retained for the period in the artifact
manifest. Every delivery and rollback directory keeps its own manifest and
points to its own exact source; a nearby branch is not a substitute.

## Current licence status: external binary distribution blocked

The inherited server history contains conflicting version evidence: the root
file is the GPLv2 text, historical program notices changed over time, and some
upstream package metadata says GPL-3.0-only. SlopTank does not claim authority
to resolve that conflict by relicensing upstream contributors. For
independently copyrightable post-fork changes owned by the SlopTank maintainer,
`LICENSE-ELECTION.md` elects GPL version 2 or any later FSF-published version.

Until the inherited-grant and linked-runtime compatibility work establishes a
supported conveyance path, SlopTank server binaries are not externally
distributed. Internal artifacts use this exact, deliberately blocking policy:

`inherited-server=version-unresolved;owner-increments=GPL-2.0-or-later;external-binary-distribution=NO-GO`

Neither an unqualified GPL-2.0-or-later label nor a GPL-3.0-only label is an
acceptable whole-repository claim. Existing project/package expressions are
component metadata; they do not prove authority over every historical
contribution. This is a conservative product gate based on observed primary
source notices and licence texts, not a warranty about every rightsholder's
grant.

## Reproducing the server artifact

Use `release-source.json`: select the committed .NET SDK via `global.json`,
restore, publish the server self-contained for `osx-x64`, add the required
licence/notices/source material, package the app, and code-sign it. Record the
actual SDK and signing identity in the artifact manifest. `Directory.Packages.props`
records central direct pins; the publish output's `jellyfin.deps.json` records
the resolved shipped graph and is covered by the final artifact digest.

Do not build from a dirty tree. Do not externally deliver a server binary
while the blocking policy above is current, even if its source manifest is
otherwise complete.
