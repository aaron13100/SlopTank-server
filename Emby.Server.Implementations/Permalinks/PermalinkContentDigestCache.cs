// SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-08, 2026-09-09.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Remembers a media file's full-content SHA-256 across process lifetimes,
/// keyed on the filesystem change token that proves the bytes are unchanged.
///
/// Hashing scales with file size: measured on production 2026-09-02, a 10.53 GB
/// film cost 397.05s to hash and a 0.11 GB episode 8.79s, roughly 26 MB/s. An
/// in-process cache alone therefore means the first play of every item after
/// every restart pays that cost again, and on a large film that is six and a
/// half minutes of saturated disk before anyone can watch anything.
///
/// The change token (device, inode, status change time, length) is durable by
/// construction, so the answer it authorizes is durable too. Keeping it only in
/// memory throws away a fact the filesystem already guarantees.
///
/// Entries are written as ordinary JSON rather than canonical JSON: unlike a
/// permalink document, a cache entry's bytes are not hashed into any identity,
/// so byte-exact serialization would be a cost with no consumer. The record is
/// versioned, tolerates unknown fields, and carries a checksum over every field
/// used to authorize reuse so a valid-looking corruption is still a miss.
///
/// Every failure is a miss, never an error: a cache that cannot be read or
/// written degrades to the full read that would have happened anyway. Each one
/// is logged with the path and the exception, because a cache that has silently
/// stopped working looks exactly like a server that is simply slow.
///
/// Entries for deleted media are not swept. Each of the 256 shards accepts at
/// most 64 entries and each entry is at most 4 KiB, so cache-created disk use
/// remains bounded without a whole-store traversal on a playback request.
/// </summary>
internal sealed class PermalinkContentDigestCache
{
    private const int CurrentVersion = 2;
    private const int MaximumEntryBytes = 4096;
    private const int DefaultMaximumEntriesPerShard = 64;
    private const string RecordKind = "permalink-content-digest";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private readonly string? _cacheRoot;
    private readonly ILogger<PermalinkContentDigestCache> _logger;
    private readonly int _maximumEntriesPerShard;
    private readonly object _writeGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkContentDigestCache"/> class.
    /// </summary>
    /// <param name="authorityRoot">
    /// The configured permalink authority root, or null when permalink
    /// authority is not configured. A null root disables persistence entirely,
    /// which leaves evidence computation behaving exactly as it did before this
    /// cache existed rather than failing.
    /// </param>
    /// <param name="logger">The logger.</param>
    /// <param name="maximumEntriesPerShard">Maximum number of cache-owned or hostile entries accepted in one shard.</param>
    public PermalinkContentDigestCache(
        string? authorityRoot,
        ILogger<PermalinkContentDigestCache> logger,
        int maximumEntriesPerShard = DefaultMaximumEntriesPerShard)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntriesPerShard);

        _cacheRoot = string.IsNullOrWhiteSpace(authorityRoot)
            ? null
            : Path.Combine(authorityRoot, ".sloptank", "content-digests");
        _logger = logger;
        _maximumEntriesPerShard = maximumEntriesPerShard;
    }

    /// <summary>
    /// Gets a value indicating whether entries are persisted at all.
    /// </summary>
    public bool IsEnabled => _cacheRoot is not null;

    /// <summary>
    /// Returns the digest recorded for a path whose change token still matches.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <param name="token">The change token read from that path just now.</param>
    /// <param name="digest">The recorded digest, when one matches.</param>
    /// <returns>True when a usable digest was found.</returns>
    public bool TryRead(string path, MacPermalinkContentIdentityToken token, out string digest)
    {
        digest = string.Empty;
        if (EntryPath(path) is not { } entryPath)
        {
            return false;
        }

        PermalinkContentDigestRecord? record;
        try
        {
            if (!File.Exists(entryPath))
            {
                return false;
            }

            if ((File.GetAttributes(entryPath) & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogWarning(
                    "Content digest entry {EntryPath} for {MediaPath} is a symbolic link; treating it as a miss",
                    entryPath,
                    path);
                return false;
            }

            using var stream = new FileStream(
                entryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: MaximumEntryBytes,
                options: FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumEntryBytes)
            {
                _logger.LogWarning(
                    "Content digest entry {EntryPath} for {MediaPath} is {EntryLength} bytes, which exceeds the accepted cache record bounds",
                    entryPath,
                    path,
                    stream.Length);
                return false;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                _logger.LogWarning(
                    "Content digest entry {EntryPath} for {MediaPath} grew while it was read; treating it as a miss",
                    entryPath,
                    path);
                return false;
            }

            record = JsonSerializer.Deserialize<PermalinkContentDigestRecord>(bytes, _jsonOptions);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            _logger.LogWarning(
                exception,
                "Content digest entry {EntryPath} for {MediaPath} could not be read; treating it as a miss and hashing the file",
                entryPath,
                path);
            return false;
        }

        if (record is null
            || record.Version != CurrentVersion
            || !string.Equals(record.Kind, RecordKind, StringComparison.Ordinal)
            || !record.Matches(path, token)
            || !IsCanonicalDigest(record.Digest)
            || !IsCanonicalTimestamp(record.CreatedAt)
            || !IsCanonicalTimestamp(record.UpdatedAt)
            || !string.Equals(record.RecordHash, ComputeRecordHash(record), StringComparison.Ordinal))
        {
            return false;
        }

        digest = record.Digest;
        return true;
    }

    /// <summary>
    /// Records the digest computed for a path at a given change token.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <param name="token">The change token captured before the read.</param>
    /// <param name="digest">The full-content digest just computed.</param>
    public void Write(string path, MacPermalinkContentIdentityToken token, string digest)
    {
        if (EntryPath(path) is not { } entryPath)
        {
            return;
        }

        if (!IsCanonicalDigest(digest))
        {
            _logger.LogWarning(
                "Content digest for {MediaPath} was not recorded because it is not a canonical SHA-256 value",
                path);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var record = new PermalinkContentDigestRecord
        {
            Version = CurrentVersion,
            Kind = RecordKind,
            Path = path,
            Device = token.Device,
            Inode = token.Inode,
            ChangeTimeSeconds = token.ChangeTimeSeconds,
            ChangeTimeNanoseconds = token.ChangeTimeNanoseconds,
            Length = token.Length,
            Digest = digest,
            CreatedAt = now,
            UpdatedAt = now,
        };
        record.RecordHash = ComputeRecordHash(record);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(record, _jsonOptions);
        if (serialized.Length > MaximumEntryBytes)
        {
            _logger.LogWarning(
                "Content digest for {MediaPath} was not recorded because its {EntryLength} byte record exceeds the cache bound",
                path,
                serialized.Length);
            return;
        }

        // Written to a sibling temporary file and renamed, so a reader never
        // sees a half-written entry. A torn entry here would authorize a wrong
        // digest for a content-addressed identity, which is worse than the slow
        // path this cache exists to avoid.
        var temporaryPath = entryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        lock (_writeGate)
        {
            try
            {
                var shardPath = Path.GetDirectoryName(entryPath)!;
                Directory.CreateDirectory(shardPath);
                if (!File.Exists(entryPath) && !TryMakeRoomInShard(shardPath))
                {
                    _logger.LogWarning(
                        "Content digest shard {ShardPath} reached its {MaximumEntries} entry bound; {MediaPath} will not be cached",
                        shardPath,
                        _maximumEntriesPerShard,
                        path);
                    return;
                }

                File.WriteAllBytes(temporaryPath, serialized);
                File.Move(temporaryPath, entryPath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Content digest for {MediaPath} could not be recorded at {EntryPath}; the next evidence computation will read the file again",
                    path,
                    entryPath);
                TryDeleteTemporary(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Forgets the recorded digest for a path whose bytes were just replaced.
    /// </summary>
    /// <param name="path">The media path.</param>
    public void Invalidate(string path)
    {
        if (EntryPath(path) is not { } entryPath)
        {
            return;
        }

        lock (_writeGate)
        {
            try
            {
                File.Delete(entryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Content digest entry {EntryPath} for {MediaPath} could not be removed; the change token still rejects it, so this is a leak of one small file rather than a stale answer",
                    entryPath,
                    path);
            }
        }
    }

    private bool TryMakeRoomInShard(string shardPath)
    {
        var count = 0;
        string? evictionCandidate = null;
        foreach (var existingPath in Directory.EnumerateFileSystemEntries(shardPath))
        {
            count++;
            if (evictionCandidate is null
                && string.Equals(Path.GetExtension(existingPath), ".json", StringComparison.Ordinal)
                && File.Exists(existingPath)
                && (File.GetAttributes(existingPath) & FileAttributes.ReparsePoint) == 0)
            {
                evictionCandidate = existingPath;
            }

            if (count >= _maximumEntriesPerShard)
            {
                break;
            }
        }

        if (count < _maximumEntriesPerShard)
        {
            return true;
        }

        if (evictionCandidate is null)
        {
            return false;
        }

        File.Delete(evictionCandidate);
        return true;
    }

    private void TryDeleteTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Abandoned content digest temporary file {TemporaryPath} could not be removed",
                temporaryPath);
        }
    }

    /// <summary>
    /// Returns where a path's entry lives, sharded so one directory does not
    /// hold an entry per media file in the library.
    /// </summary>
    private string? EntryPath(string path)
    {
        if (_cacheRoot is null)
        {
            return null;
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        return Path.Combine(_cacheRoot, key[..2], key + ".json");
    }

    private static bool IsCanonicalDigest(string? value)
    {
        if (value is null
            || value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiHexDigitLower(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalTimestamp(string? value)
        => DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static string ComputeRecordHash(PermalinkContentDigestRecord record)
    {
        var material = string.Join(
            '\0',
            record.Version.ToString(CultureInfo.InvariantCulture),
            record.Kind,
            record.Path,
            record.Device.ToString(CultureInfo.InvariantCulture),
            record.Inode.ToString(CultureInfo.InvariantCulture),
            record.ChangeTimeSeconds.ToString(CultureInfo.InvariantCulture),
            record.ChangeTimeNanoseconds.ToString(CultureInfo.InvariantCulture),
            record.Length.ToString(CultureInfo.InvariantCulture),
            record.Digest,
            record.CreatedAt,
            record.UpdatedAt);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// One recorded digest and the change token that authorizes reusing it.
    /// </summary>
    private sealed class PermalinkContentDigestRecord
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("device")]
        public int Device { get; set; }

        [JsonPropertyName("inode")]
        public ulong Inode { get; set; }

        [JsonPropertyName("change_time_seconds")]
        public long ChangeTimeSeconds { get; set; }

        [JsonPropertyName("change_time_nanoseconds")]
        public long ChangeTimeNanoseconds { get; set; }

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("digest")]
        public string Digest { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonPropertyName("record_hash")]
        public string RecordHash { get; set; } = string.Empty;

        /// <summary>
        /// Returns whether this record still describes the file at hand.
        ///
        /// The path is compared as well as the token because entries are keyed
        /// by a hash of the path: a hash collision would otherwise hand back
        /// another file's digest, and for a content-addressed identity that is
        /// the one failure worse than being slow.
        /// </summary>
        public bool Matches(string path, MacPermalinkContentIdentityToken token)
            => string.Equals(Path, path, StringComparison.Ordinal)
                && Device == token.Device
                && Inode == token.Inode
                && ChangeTimeSeconds == token.ChangeTimeSeconds
                && ChangeTimeNanoseconds == token.ChangeTimeNanoseconds
                && Length == token.Length;
    }
}
