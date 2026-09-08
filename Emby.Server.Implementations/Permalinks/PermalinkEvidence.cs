using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Computes canonical full-content and aggregate evidence without display metadata.
/// </summary>
public sealed class PermalinkEvidence
{
    private const int DefaultContentDigestMemoryLimit = 4096;
    private const int ContentDigestGateCount = 256;

    /// <summary>
    /// Caches the full-content SHA-256 for a path, valid as long as its macOS
    /// change token (device, inode, status change time, length -- see
    /// <see cref="MacPermalinkContentIdentity"/>) has not changed. Hashing
    /// scales with file size (a multi-GB movie costs tens of seconds), so a
    /// repeat call for an unchanged file must cost a stat, not another full
    /// read. Size and modification time alone are not a valid cache key
    /// (permalink-url-design.md): both survive a content-preserving
    /// same-second replacement, since modification time can be reset by the
    /// very call that replaced the bytes. A path whose change token cannot be
    /// read (non-macOS host, or the stat call fails) is never cached and is
    /// hashed on every call, per the same design rule.
    /// </summary>
    private readonly Dictionary<string, ContentDigestCacheEntry> _contentDigestCache = new(StringComparer.Ordinal);
    private readonly object _contentDigestCacheGate = new();
    private readonly SemaphoreSlim[] _contentDigestGates = Enumerable.Range(0, ContentDigestGateCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private readonly PermalinkContentReadMeter _contentReads;

    private readonly PermalinkContentDigestCache _digestCache;
    private readonly int _contentDigestMemoryLimit;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkEvidence"/> class.
    ///
    /// Internal because <see cref="PermalinkContentDigestCache"/> is, and every
    /// consumer of this type lives in this assembly. Registered through an
    /// explicit factory in ApplicationHost rather than by convention.
    /// </summary>
    /// <param name="contentReads">Records each full media read this class performs.</param>
    /// <param name="digestCache">Remembers digests across process lifetimes.</param>
    /// <param name="contentDigestMemoryLimit">Maximum number of completed digests retained in memory.</param>
    internal PermalinkEvidence(
        PermalinkContentReadMeter contentReads,
        PermalinkContentDigestCache digestCache,
        int contentDigestMemoryLimit = DefaultContentDigestMemoryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentDigestMemoryLimit);

        _contentReads = contentReads;
        _digestCache = digestCache;
        _contentDigestMemoryLimit = contentDigestMemoryLimit;
    }

    /// <summary>
    /// Gets a value indicating whether digests survive a restart.
    ///
    /// False when permalink authority is unconfigured, in which case there is
    /// nothing for a precompute pass to fill and it should not read the library.
    /// </summary>
    public bool PersistsContentDigests => _digestCache.IsEnabled;

    /// <summary>
    /// Discards the cached digest for a path whose bytes were just replaced in
    /// place. The change token already invalidates a stale entry on its own
    /// (a same-path content swap changes status change time even when
    /// modification time is explicitly reset), so this is defense in depth,
    /// not the correctness mechanism: every caller that overwrites a stable
    /// item path still calls this before the next evidence computation so a
    /// lookup never depends on stat timing precision.
    /// </summary>
    /// <param name="path">The path whose on-disk content just changed.</param>
    public void InvalidateContentDigest(string path)
    {
        ForgetContentDigest(path);
        _digestCache.Invalidate(path);
    }

    /// <summary>
    /// Returns whether this path's digest is already known, without reading a
    /// single media byte.
    ///
    /// Exists so a precompute pass can skip what is already done at the cost of
    /// a stat. Callers get an answer about the work rather than a handle on the
    /// cache, which stays an implementation detail of this class.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <returns>True when evidence for this path would cost no full read.</returns>
    public bool IsContentDigestKnown(string path)
    {
        if (MacPermalinkContentIdentity.TryRead(path) is not { } token)
        {
            return false;
        }

        if (TryGetContentDigest(path, out var cached) && cached.MatchesToken(token))
        {
            return true;
        }

        return _digestCache.TryRead(path, token, out _);
    }

    /// <summary>
    /// Computes canonical evidence with an operation-owned replacement main file.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="mainPath">The main path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkEvidenceResult> ComputeMediaReplacementAsync(
        BaseItem item,
        string mainPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (item is not Video video)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Ineligible,
                    "content-shape",
                    $"{item.GetType().Name} has no supported replacement media shape.");
            }

            var paths = new List<(string Role, string Path)> { ("main", mainPath) };
            for (var index = 0; index < video.AdditionalParts.Length; index++)
            {
                paths.Add(($"additional:{index}", video.AdditionalParts[index]));
            }

            var leaves = new List<PermalinkLeaf>(paths.Count);
            foreach (var (role, path) in paths)
            {
                leaves.Add(await HashFileAsync(
                    "media",
                    role,
                    relativePath: null,
                    path,
                    cancellationToken).ConfigureAwait(false));
            }

            return Build(leaves);
        }
        catch (FileNotFoundException exception)
        {
            throw Unreachable(mainPath, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw Unreachable(mainPath, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Unreachable(mainPath, exception);
        }
        catch (IOException exception)
        {
            throw Unreachable(mainPath, exception);
        }
    }

    /// <summary>
    /// Computes file, multipart, or optical evidence for a content item.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkEvidenceResult> ComputeContentItemAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            if (item is Video { VideoType: VideoType.Dvd or VideoType.BluRay })
            {
                return await ComputeOpticalAsync(item, cancellationToken).ConfigureAwait(false);
            }

            if (item is not Video video || string.IsNullOrEmpty(item.Path))
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Ineligible,
                    "content-shape",
                    $"{item.GetType().Name} has no supported local content shape.");
            }

            var paths = new List<(string Role, string Path)> { ("main", item.Path) };
            for (var index = 0; index < video.AdditionalParts.Length; index++)
            {
                paths.Add(($"additional:{index}", video.AdditionalParts[index]));
            }

            var leaves = new List<PermalinkLeaf>(paths.Count);
            foreach (var (role, path) in paths)
            {
                leaves.Add(await HashFileAsync(
                    "media",
                    role,
                    relativePath: null,
                    path,
                    cancellationToken).ConfigureAwait(false));
            }

            return Build(leaves);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw Unreachable(item.Path, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw Unreachable(item.Path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Unreachable(item.Path, exception);
        }
        catch (IOException exception)
        {
            throw Unreachable(item.Path, exception);
        }
    }

    /// <summary>
    /// Computes a canonical descendant manifest for Series and Season.
    /// </summary>
    /// <param name="descendants">The descendants.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEvidenceResult ComputeDescendantManifest(
        IReadOnlyList<(BaseItem Item, PermalinkStoredState State)> descendants)
    {
        var leaves = descendants
            .Select(pair => new PermalinkLeaf(
                "descendant",
                pair.Item.GetType().Name,
                pair.State.CapsuleId.ToString("D"),
                pair.State.ContentRoot,
                0,
                1))
            .OrderBy(leaf => leaf.Path, StringComparer.Ordinal)
            .ToArray();
        return Build(leaves);
    }

    /// <summary>
    /// Computes a finite direct-member graph manifest for one BoxSet root.
    /// </summary>
    /// <param name="rootCapsuleId">The root capsule id.</param>
    /// <param name="members">The members.</param>
    /// <returns>The resulting value.</returns>
    public PermalinkEvidenceResult ComputeBoxSetManifest(
        Guid rootCapsuleId,
        IReadOnlyList<(BaseItem Item, PermalinkStoredState State)> members)
    {
        var leaves = new List<PermalinkLeaf>
        {
            new(
                "boxset-node",
                "root",
                rootCapsuleId.ToString("D"),
                "sha256:" + Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(rootCapsuleId.ToString("D")))),
                0,
                1)
        };
        leaves.AddRange(members.Select(pair => new PermalinkLeaf(
            "boxset-member",
            pair.Item.GetType().Name,
            pair.State.CapsuleId.ToString("D"),
            pair.State.ContentRoot,
            0,
            1)));
        return Build(leaves);
    }

    private async Task<PermalinkEvidenceResult> ComputeOpticalAsync(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
        {
            throw new DirectoryNotFoundException(item.Path);
        }

        var mediaTree = Directory.Exists(Path.Combine(item.Path, "BDMV"))
            ? Path.Combine(item.Path, "BDMV")
            : Path.Combine(item.Path, "VIDEO_TS");
        if (!Directory.Exists(mediaTree))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "optical-root-missing",
                $"Optical item '{item.Path}' has neither BDMV nor VIDEO_TS.");
        }

        var files = Directory.EnumerateFiles(mediaTree, "*", SearchOption.AllDirectories)
            .OrderBy(path => NormalizeRelative(item.Path, path), StringComparer.Ordinal)
            .ToArray();
        var leaves = new List<PermalinkLeaf>(files.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "optical-symlink",
                    $"Optical manifest refuses symlink '{file}'.");
            }

            var relative = NormalizeRelative(item.Path, file);
            if (!seen.Add(relative))
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "optical-case-collision",
                    $"Optical manifest has a case-colliding path '{relative}'.");
            }

            leaves.Add(await HashFileAsync(
                "optical",
                item.GetType().Name,
                relative,
                file,
                cancellationToken).ConfigureAwait(false));
        }

        return Build(leaves);
    }

    private async Task<PermalinkLeaf> HashFileAsync(
        string kind,
        string role,
        string? relativePath,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = _contentDigestGates[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % ContentDigestGateCount];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await HashFileCoreAsync(kind, role, relativePath, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PermalinkLeaf> HashFileCoreAsync(
        string kind,
        string role,
        string? relativePath,
        string path,
        CancellationToken cancellationToken)
    {
        var token = MacPermalinkContentIdentity.TryRead(path);
        string digest;
        long length;

        if (token is { } current
            && TryGetContentDigest(path, out var cached)
            && cached.MatchesToken(current))
        {
            digest = cached.Digest;
            length = current.Length;
        }
        else if (token is { } durable
            && _digestCache.TryRead(path, durable, out var recorded))
        {
            // The durable tier is what survives a restart. Without it the first
            // play of every item after every restart pays the full read again,
            // which on a multi-GB film is minutes of saturated disk before
            // anyone can watch anything. Promote the answer back into the
            // in-process tier so repeats within this process cost a stat.
            digest = recorded;
            length = durable.Length;
            RememberContentDigest(path, new ContentDigestCacheEntry(durable, digest));
        }
        else
        {
            _contentReads.RecordFullContentRead();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            length = stream.Length;
            digest = "sha256:" + Convert.ToHexStringLower(hash);

            var tokenAfterRead = MacPermalinkContentIdentity.TryRead(path);
            if (token is { } beforeRead && tokenAfterRead is { } afterRead && beforeRead == afterRead)
            {
                RememberContentDigest(path, new ContentDigestCacheEntry(afterRead, digest));
                _digestCache.Write(path, afterRead, digest);
            }
            else
            {
                // No readable change token means nothing authorizes reuse, so
                // neither tier may keep an answer for this path.
                ForgetContentDigest(path);
                _digestCache.Invalidate(path);
                if (token != tokenAfterRead
                    && (token is not null || tokenAfterRead is not null))
                {
                    throw new IOException($"Media changed while its content identity was being computed: '{path}'.");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var leafBytes = Encoding.UTF8.GetBytes(
            $"{kind}\0{role}\0{relativePath}\0{length}\0{digest}");
        return new PermalinkLeaf(
            kind,
            role,
            relativePath,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(leafBytes)),
            length,
            1);
    }

    private bool TryGetContentDigest(string path, out ContentDigestCacheEntry entry)
    {
        lock (_contentDigestCacheGate)
        {
            return _contentDigestCache.TryGetValue(path, out entry!);
        }
    }

    private void RememberContentDigest(string path, ContentDigestCacheEntry entry)
    {
        lock (_contentDigestCacheGate)
        {
            if (!_contentDigestCache.ContainsKey(path)
                && _contentDigestCache.Count >= _contentDigestMemoryLimit)
            {
                // Clearing is deliberately simple and bounded. The durable
                // tier keeps evicted entries O(1), so an LRU would add state
                // and lock complexity without avoiding a media read.
                _contentDigestCache.Clear();
            }

            _contentDigestCache[path] = entry;
        }
    }

    private void ForgetContentDigest(string path)
    {
        lock (_contentDigestCacheGate)
        {
            _contentDigestCache.Remove(path);
        }
    }

    private static PermalinkEvidenceResult Build(IReadOnlyList<PermalinkLeaf> source)
    {
        var leaves = source
            .OrderBy(leaf => leaf.Kind, StringComparer.Ordinal)
            .ThenBy(leaf => leaf.Role, StringComparer.Ordinal)
            .ThenBy(leaf => leaf.Path, StringComparer.Ordinal)
            .ThenBy(leaf => leaf.Digest, StringComparer.Ordinal)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var leaf in leaves)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{leaf.Kind}\0{leaf.Role}\0{leaf.Path}\0{leaf.Digest}\0{leaf.ByteLength}\0{leaf.Count}\n"));
        }

        return new PermalinkEvidenceResult(
            "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()),
            leaves);
    }

    private static string NormalizeRelative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static PermalinkException Unreachable(string path, Exception exception)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "content-unreachable",
            $"Permalink content is unreachable at '{path}' ({exception.Message}).",
            exception);
    }

    /// <summary>One cached full-content digest, valid while the captured change token still matches.</summary>
    private sealed record ContentDigestCacheEntry(MacPermalinkContentIdentityToken Token, string Digest)
    {
        public bool MatchesToken(MacPermalinkContentIdentityToken current) => Token == current;
    }
}
