using System;

namespace Jellyfin.Server
{
    /// <summary>
    /// Decides the <c>Cache-Control</c> header for one file served from the
    /// hosted web client.
    /// </summary>
    /// <remarks>
    /// This lives outside <see cref="Startup"/> so the policy can be exercised
    /// without booting Kestrel. The rule it encodes is a safety property: an
    /// asset is frozen in a browser cache ONLY when its own URL carries a token
    /// that changes whenever its bytes change, so a deploy can never be shadowed
    /// by a stale copy. Anything else revalidates.
    /// </remarks>
    public static class WebClientCachePolicy
    {
        /// <summary>
        /// Header value for an asset the browser must revalidate every time.
        /// </summary>
        public const string Revalidate = "no-cache";

        /// <summary>
        /// Header value for an asset whose URL changes whenever its bytes do.
        /// </summary>
        public const string Immutable = "public, max-age=31536000, immutable";

        /// <summary>
        /// The web client entry document, which must never be frozen.
        /// </summary>
        private const string IndexFileName = "index.html";

        /// <summary>
        /// The service worker script, which must never be frozen: a stale one
        /// intercepts every other request, so it would outlive the deploy for
        /// every asset rather than just for itself.
        /// </summary>
        private const string ServiceWorkerFileName = "serviceworker.js";

        /// <summary>
        /// Shortest run of hex that counts as a webpack content hash. The build
        /// emits 20; requiring 16 keeps short hex-looking names such as
        /// "abcdef.css" from being mistaken for hashed output.
        /// </summary>
        private const int MinimumContentHashLength = 16;

        /// <summary>
        /// Resolves the <c>Cache-Control</c> value for a served web-client file.
        /// </summary>
        /// <param name="fileName">File name being served, without any directory part.</param>
        /// <param name="queryString">Raw query string of the request, including the leading '?' when present.</param>
        /// <returns>The header value to send. Never null: every asset gets an explicit policy.</returns>
        public static string Resolve(string fileName, string? queryString)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            // Checked before the token rule, not after: these two govern how a
            // deploy reaches the user, so a query string must never be able to
            // talk either of them into being immutable.
            if (string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, ServiceWorkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                return Revalidate;
            }

            return HasCacheBustingToken(fileName, queryString) ? Immutable : Revalidate;
        }

        /// <summary>
        /// Whether this URL changes whenever the bytes behind it change.
        /// </summary>
        /// <param name="fileName">File name being served.</param>
        /// <param name="queryString">Raw query string, including any leading '?'.</param>
        /// <returns>True when the URL carries a cache-busting token.</returns>
        private static bool HasCacheBustingToken(string fileName, string? queryString)
        {
            // HtmlWebpackPlugin({ hash: true }) stamps the per-build compilation
            // hash as the query on every entry script and stylesheet, so those
            // keep a stable file name but a URL that moves every build.
            if (!string.IsNullOrEmpty(queryString)
                && queryString.AsSpan().TrimStart('?').Length > 0)
            {
                return true;
            }

            // Lazy chunks and extracted CSS instead carry [contenthash] in the
            // name, and the webpack runtime requests them with no query at all.
            return HasContentHashSegment(fileName);
        }

        /// <summary>
        /// Whether the file name contains a webpack content-hash segment.
        /// </summary>
        /// <param name="fileName">File name being served.</param>
        /// <returns>True when a dot-separated segment is a long run of hex.</returns>
        private static bool HasContentHashSegment(string fileName)
        {
            foreach (var segment in fileName.Split('.'))
            {
                if (segment.Length >= MinimumContentHashLength && IsHex(segment))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether every character is a hexadecimal digit.
        /// </summary>
        /// <param name="value">Candidate segment.</param>
        /// <returns>True when the segment is entirely hex.</returns>
        private static bool IsHex(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
