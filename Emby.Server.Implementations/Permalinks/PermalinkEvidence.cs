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

    private static async Task<PermalinkEvidenceResult> ComputeOpticalAsync(
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

    private static async Task<PermalinkLeaf> HashFileAsync(
        string kind,
        string role,
        string? relativePath,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var fullDigest = "sha256:" + Convert.ToHexStringLower(digest);
        var leafBytes = Encoding.UTF8.GetBytes(
            $"{kind}\0{role}\0{relativePath}\0{stream.Length}\0{fullDigest}");
        return new PermalinkLeaf(
            kind,
            role,
            relativePath,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(leafBytes)),
            stream.Length,
            1);
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
}
