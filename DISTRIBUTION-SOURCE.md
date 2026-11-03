<!-- SlopTank modification notice: added or changed by SlopTank on 2026-09-07, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-04. -->
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

## Candidate licence election and distribution status

The current server candidate exercises GNU GPL version 3 under SlopTank's
interpretation of the existing program grant. That interpretation reads the
current affirmative versionless assembly grants and separately scoped v3
package notices in the context of their history and GPLv2 section 9. It is
not a new whole-history licence, a contributor copyright assignment, or a
claim that dependencies create permission to relicense upstream code.

The historical version-2 program notice and intervening contributor context
remain adverse evidence. SlopTank does not claim universal contributor
consent. Any specifically identified restrictive grant tied to retained
material must be traced and resolved. For independently copyrightable
post-fork changes owned by the SlopTank maintainer, `LICENSE-ELECTION.md`
continues to elect GPL version 2 or any later FSF-published version. The
unmodified root `LICENSE` text and component-specific grants are retained.

This is a candidate binary election, not clearance of an artifact. Until the
exact dependency, source, notice, installer and qualification requirements
are met, server binaries are not externally
distributed. The enforced manifest policy remains deliberately blocking:

`inherited-server=current-grant-interpreted-for-GPL-3.0-candidate;owner-increments=GPL-2.0-or-later;candidate-binary-election=GPL-3.0-only;external-binary-distribution=NO-GO`

Neither a blanket GPL-3.0-only repository label nor the maintainer's own
or-later election changes inherited copyright permissions. The separate SVG
converter has its own MIT source licence and dependency notices; its
standard file interface does not by itself clear the final distribution.

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
