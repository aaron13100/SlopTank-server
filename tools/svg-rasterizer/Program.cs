// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
// SPDX-License-Identifier: MIT
// Standalone file converter. No server assemblies or internal application protocol.
using System.Text;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace SvgRasterizer;

/// <summary>Converts one self-contained SVG file to a transparent PNG.</summary>
internal static class Program
{
    private const long MaximumInputBytes = 16 * 1024 * 1024;
    private const long MaximumPixels = 64 * 1024 * 1024;

    /// <summary>Accepts an input SVG and a new output PNG; errors go to stderr.</summary>
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("[usage] svg-rasterizer INPUT.svg OUTPUT.png");
            return 2;
        }

        bool ownsOutput = false;
        bool completed = false;
        try
        {
            SvgDocument.DisableDtdProcessing = true;
            SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
            SvgDocument.ResolveExternalImages = ExternalType.None;
            SvgDocument.ResolveExternalElements = ExternalType.None;

            using var input = File.OpenRead(arguments[0]);
            if (input.Length > MaximumInputBytes)
            {
                throw new InvalidDataException("[svg_input_limit] SVG input exceeds 16 MiB.");
            }

            var document = ReadSelfContainedSvg(input, 0);
            using var sanitized = new MemoryStream();
            document.Save(sanitized);
            sanitized.Position = 0;
            using var svg = new SKSvg();
            var picture = svg.Load(sanitized)
                ?? throw new InvalidDataException("[svg_render_empty] The SVG renderer returned no picture.");
            var bounds = picture.CullRect;
            if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
                || bounds.Width <= 0 || bounds.Height <= 0
                || bounds.Width > MaximumPixels || bounds.Height > MaximumPixels)
            {
                throw new InvalidDataException("[svg_dimensions_invalid] SVG dimensions must be finite and positive.");
            }

            int width = checked((int)Math.Ceiling(bounds.Width));
            int height = checked((int)Math.Ceiling(bounds.Height));
            if ((long)width * height > MaximumPixels)
            {
                throw new InvalidDataException("[svg_pixel_limit] SVG output exceeds 64 megapixels.");
            }

            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawPicture(picture);
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException("[png_encode_failed] PNG encoding returned no data.");
            using (var output = new FileStream(arguments[1], FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsOutput = true;
                png.SaveTo(output);
            }

            completed = true;
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[svg_conversion_failed] {error.GetType().Name}: {error.Message}");
            return 1;
        }
        finally
        {
            if (ownsOutput && !completed)
            {
                try
                {
                    File.Delete(arguments[1]);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[png_cleanup_failed] {error.GetType().Name}: {error.Message}");
                }
            }
        }
    }

    /// <summary>Validates references, including embedded SVGs, before rendering.</summary>
    private static XDocument ReadSelfContainedSvg(Stream input, int depth)
    {
        if (depth > 16 || input.Length > MaximumInputBytes)
        {
            throw new InvalidDataException("[svg_input_limit] Embedded SVG exceeds the input size or nesting limit.");
        }

        // Strip ignored DTD declarations before handing XML to the renderer.
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumInputBytes,
        });
        var document = XDocument.Load(reader);
        if (document.Root is not { } root || root.Name.LocalName != "svg"
            || (root.Name.NamespaceName.Length != 0 && root.Name.NamespaceName != "http://www.w3.org/2000/svg"))
        {
            throw new InvalidDataException("[svg_root_invalid] Expected an SVG document.");
        }

        // The pinned Svg.Model loader reads image hrefs before consulting the
        // SvgDocument resource flags. Validate those references directly, with
        // no host lists, URL-prefix tricks or operating-system sandbox.
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(value => value.Name.LocalName == "href"))
            {
                var reference = attribute.Value.Trim();
                if (reference.Length == 0 || reference.StartsWith('#') || element.Name.LocalName == "a")
                {
                    continue;
                }

                int comma = reference.IndexOf(',', StringComparison.Ordinal);
                if (!reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
                {
                    throw new InvalidDataException("[svg_external_resource] External resources are disabled; embed image data in the SVG.");
                }

                var header = reference[5..comma];
                var mime = header.Split(';')[0].Trim().ToLowerInvariant();
                var payload = reference[(comma + 1)..];
                bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
                var bytes = base64 ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                // Svg.Model also treats gzipped image/png (and other image
                // MIME types) as SVG, so inspect the bytes rather than trust
                // only the declared MIME type.
                bool compressed = base64 && bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;
                if (mime != "image/svg+xml" && !compressed)
                {
                    if (mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("img/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new InvalidDataException("[svg_embedded_type] An embedded resource must be an image.");
                }

                if (compressed)
                {
                    bytes = ExpandSvg(bytes);
                }

                using var nested = new MemoryStream(bytes, writable: false);
                var nestedDocument = ReadSelfContainedSvg(nested, depth + 1);
                using var sanitized = new MemoryStream();
                nestedDocument.Save(sanitized);
                attribute.Value = "data:image/svg+xml;base64," + Convert.ToBase64String(sanitized.ToArray());
            }
        }

        return document;
    }

    /// <summary>Expands embedded SVGZ with the same bound as ordinary input.</summary>
    private static byte[] ExpandSvg(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var expanded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = gzip.Read(buffer)) != 0)
        {
            if (expanded.Length + count > MaximumInputBytes)
            {
                throw new InvalidDataException("[svg_input_limit] Expanded SVG exceeds 16 MiB.");
            }

            expanded.Write(buffer, 0, count);
        }

        return expanded.ToArray();
    }
}
