<!-- SlopTank modification notice: added or changed by SlopTank on 2026-11-03. -->
# SVG rasterizer

A standalone converter with ordinary file input and PNG output:

```text
svg-rasterizer INPUT.svg OUTPUT.png
```

The output must not already exist. Success exits with code 0; invalid arguments
exit with code 2; conversion failures exit with code 1 and retain the actual
exception type and message on stderr. Failed writes remove their partial output.

The converter supports self-contained SVG artwork, including styles, text,
internal references and embedded image data. It does not fetch external file or
network image resources. DTD declarations are ignored and external XML entities
are disabled. Input is limited to 16 MiB and output to 64 megapixels. The calling
application must bound the lifetime of the child process.

The converter checks image references before rendering, including references in
embedded SVG images (up to 16 nesting levels). Its separate `SvgDocument`
resource flags do not cover the pinned renderer's `Svg.Model` image loader by
themselves. External references fail conversion before that loader runs. Gzipped
embedded images are checked as SVG regardless of their declared image MIME type,
matching the renderer's actual behavior; decompression has the same 16 MiB bound.

Publish this project independently, self-contained for the target runtime, into
the server package's `svg-rasterizer/` directory. Its runtime and dependencies
remain in that directory. Do not reference this project or its rendering packages
from the server's managed assembly graph. The server consumes a PNG; the two
programs exchange no server types, metadata objects or private protocol.

Original converter code is MIT licensed. Dependencies keep their own grants,
including the MS-PL `Svg.Custom` dependency. Retain their notices and the exact
converter source and build inputs with the release materials. This directory's
MIT licence does not change any dependency's licence or clear a final release.
