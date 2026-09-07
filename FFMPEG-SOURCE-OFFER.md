# FFmpeg corresponding source offer

SlopTank distributions that include the macOS x86_64 FFmpeg binary described
below provide it under GNU GPL version 3 or, at your option, any later version.
The complete GPLv3 text accompanies the SlopTank server artifact as
`FFMPEG-GPL-3.0.txt`; the original Jellyfin application also places it beside
the FFmpeg distribution as `ffmpeg LICENSE.txt`.

## Covered binary

- reported version: `7.1.3-Jellyfin`
- platform: macOS x86_64
- SlopTank-distributed, Apple-signed SHA-256:
  `8be1dcdd554cbc590d8caaf4c138478f5033d80830eb23aa5028472c5a642884`
- official unsigned FFmpeg executable SHA-256:
  `67e18c593922f9e13d112c178244cb858615944a034797590882656ffdc7068a`
- official release archive SHA-256:
  `0da2f6beb902ddc0c04ec2433f8306a6f59db653d71b6858395d81a9339d62fa`

The signed executable is Jellyfin's official unsigned executable with an Apple
Developer ID signature added by the macOS application distributor. Removing
that signature leaves only the expected Mach-O signing-header changes and
padding.

## Corresponding source and build material

The exact source revision, patches, dependency pins, and macOS build recipe are
in Jellyfin's `jellyfin-ffmpeg` tag `v7.1.3-1`, commit
`bc933287ba29bc1867c438eb7142f6dd3eb25733`:

- source and build archive:
  <https://github.com/jellyfin/jellyfin-ffmpeg/archive/refs/tags/v7.1.3-1.tar.gz>
- source repository at the exact tag:
  <https://github.com/jellyfin/jellyfin-ffmpeg/tree/v7.1.3-1>
- macOS build instructions:
  <https://github.com/jellyfin/jellyfin-ffmpeg/blob/v7.1.3-1/builder/Buildmac.md>
- macOS build entry point:
  <https://github.com/jellyfin/jellyfin-ffmpeg/blob/v7.1.3-1/builder/buildmac.sh>

The enabled static dependencies are each identified by an immutable commit,
tag, or Subversion revision in `builder/scripts.d/` at that tag. This includes
the GPL codec libraries specifically:

- x264 commit `0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee`:
  <https://code.videolan.org/videolan/x264/-/archive/0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee/x264-0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee.tar.gz>
- x265 commit `fa2770934b8f3d88aa866c77f27cb63f69a9ed39`:
  <https://bitbucket.org/multicoreware/x265_git/get/fa2770934b8f3d88aa866c77f27cb63f69a9ed39.tar.gz>

Those links may be hosted by third parties, but SlopTank remains responsible
for source availability. If a link fails, open an issue at
<https://github.com/aaron13100/SlopTank-server/issues>. SlopTank will provide
the covered corresponding source through a network server at no charge. This
offer remains valid for at least three years after SlopTank last conveys this
binary, and for as long as SlopTank offers support for the product model.

## Installing a rebuilt binary

The application does not prevent replacement with a modified build. Build the
macOS x86_64 executable using the material above, stop the SlopTank service,
set its existing `--ffmpeg` launch argument to the rebuilt executable, and
start the service again. The executable must remain readable and executable by
the service account. No SlopTank signing key, authorization token, or other
secret is required to run a modified FFmpeg build.

No patent licence beyond the grants, conditions, and protections in GPLv3 and
the covered components' own licences is asserted by this offer.
