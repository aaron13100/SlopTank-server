using System;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Serializes immutable versioned permalink records into canonical UTF-8 JSON.
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes one durable document.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="value">The document to serialize.</param>
    /// <returns>The canonical UTF-8 JSON bytes.</returns>
    public static ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    /// <summary>
    /// Deserializes one durable document and preserves its parse failure context.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="value">The UTF-8 JSON bytes.</param>
    /// <param name="path">The source path used in parse diagnostics.</param>
    /// <returns>The deserialized document.</returns>
    public static T Deserialize<T>(ReadOnlySpan<byte> value, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, _options)
                ?? throw new JsonException("JSON document was null.");
        }
        catch (JsonException exception)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "malformed-document",
                $"Permalink document '{path}' is malformed ({exception.Message}).",
                exception);
        }
    }

    /// <summary>
    /// Computes a prefixed SHA-256 digest of exact canonical bytes.
    /// </summary>
    /// <param name="bytes">The bytes to hash.</param>
    /// <returns>The lowercase prefixed SHA-256 digest.</returns>
    public static string Digest(ReadOnlyMemory<byte> bytes)
    {
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
    }
}
