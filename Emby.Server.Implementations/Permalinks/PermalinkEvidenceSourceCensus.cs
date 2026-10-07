// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using EvidenceKind = Emby.Server.Implementations.Permalinks.PermalinkEvidenceRegistryStore.PermalinkPopulationEvidenceKind;
using SourceKind = Emby.Server.Implementations.Permalinks.PermalinkEvidenceRegistryStore.PermalinkPopulationSourceKind;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Builds deterministic, pinned and no-follow inventories for one population source.</summary>
internal sealed class PermalinkEvidenceSourceCensus : IPermalinkEvidenceSourceCensus
{
    private readonly IPermalinkPathObjects _objects;

    /// <summary>Initializes a new instance of the <see cref="PermalinkEvidenceSourceCensus"/> class.</summary>
    /// <param name="objects">The no-follow filesystem inspector.</param>
    public PermalinkEvidenceSourceCensus(IPermalinkPathObjects objects)
    {
        _objects = objects;
    }

    /// <summary>Reads every regular file in one exact source tree in lexical order.</summary>
    /// <param name="request">The pinned root and source subtree.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deterministic source generation and evidence entries.</returns>
    public async Task<Result> ReadAsync(Request request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var sourceRoot = Path.GetFullPath(request.SourceRoot);
            var scanRoot = Path.GetFullPath(request.ScanRoot);
            RequireContained(sourceRoot, scanRoot);
            var rootBefore = RequireUnlinkedObject(sourceRoot, "registry-population-root-unavailable");
            var entries = new List<Entry>();
            if (TryGetAttributes(scanRoot, out var attributes))
            {
                RequireDirectory(scanRoot, attributes);
                await ReadDirectoryAsync(request, sourceRoot, scanRoot, entries, cancellationToken)
                    .ConfigureAwait(false);
            }

            var rootAfter = RequireUnlinkedObject(sourceRoot, "registry-population-root-unavailable");
            if (!string.Equals(rootBefore.Identity, rootAfter.Identity, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "registry-population-root-changed",
                    $"Population root '{sourceRoot}' changed during its census.");
            }

            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.SourceKey, right.SourceKey));
            return new Result(Digest(request, entries), entries);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Unavailable(
                "registry-population-source-unreadable",
                $"Population source '{request.ScanRoot}' could not be read ({exception.Message}).",
                exception);
        }
    }

    private async Task ReadDirectoryAsync(
        Request request,
        string sourceRoot,
        string directory,
        List<Entry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                     .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Unavailable(
                    "registry-population-source-linked",
                    $"Population source entry '{path}' is a symbolic link and was refused.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                await ReadDirectoryAsync(request, sourceRoot, path, entries, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var relative = NormalizeRelative(sourceRoot, path);
            if (string.Equals(relative, PermalinkRootMarker.FileName, StringComparison.Ordinal))
            {
                continue;
            }

            var before = RequireUnlinkedObject(path, "registry-population-source-disappeared");
            await using var stream = _objects.OpenReadNoFollowExact(path, out var openedIdentity)
                ?? throw Unavailable(
                    "registry-population-source-disappeared",
                    $"Population source entry '{path}' disappeared before it could be opened.");
            if (!string.Equals(before.Identity, openedIdentity, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "registry-population-source-changed",
                    $"Population source entry '{path}' changed before it could be opened.");
            }

            var length = stream.Length;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var after = RequireUnlinkedObject(path, "registry-population-source-disappeared");
            if (!string.Equals(openedIdentity, after.Identity, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "registry-population-source-changed",
                    $"Population source entry '{path}' changed while it was being read.");
            }

            entries.Add(new Entry(
                Classify(request.Kind, relative),
                relative,
                relative,
                OperationId(request.Kind, relative),
                openedIdentity,
                length,
                sha256));
        }
    }

    private PermalinkPinnedObject RequireUnlinkedObject(string path, string code)
    {
        var pinned = _objects.ReadPinnedObjectNoFollowExact(path)
            ?? throw Unavailable(code, $"Population source entry '{path}' is absent.");
        if (pinned.IsSymbolicLink)
        {
            throw Unavailable(
                "registry-population-source-linked",
                $"Population source entry '{path}' is a symbolic link and was refused.");
        }

        return pinned;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void RequireDirectory(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Unavailable(
                "registry-population-source-linked",
                $"Population source '{path}' is a symbolic link and was refused.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Unavailable(
                "registry-population-source-invalid",
                $"Population source '{path}' is not a directory.");
        }
    }

    private static void RequireContained(string sourceRoot, string scanRoot)
    {
        var relative = Path.GetRelativePath(sourceRoot, scanRoot);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The scan root must be contained by its pinned source root.", nameof(scanRoot));
        }
    }

    private static string NormalizeRelative(string sourceRoot, string path)
        => Path.GetRelativePath(sourceRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Normalize(NormalizationForm.FormC);

    private static EvidenceKind Classify(SourceKind kind, string relativePath)
    {
        if (kind is SourceKind.JournalHot or SourceKind.JournalArchive)
        {
            if (relativePath.Contains("/staging/", StringComparison.Ordinal))
            {
                return EvidenceKind.JournalStaging;
            }

            if (relativePath.Contains("/quarantine/", StringComparison.Ordinal))
            {
                return EvidenceKind.JournalQuarantine;
            }
        }

        return EvidenceKind.EvidenceFile;
    }

    private static string? OperationId(SourceKind kind, string relativePath)
    {
        if (kind is not (SourceKind.JournalHot or SourceKind.JournalArchive))
        {
            return null;
        }

        foreach (var component in relativePath.Split('/'))
        {
            if (Guid.TryParseExact(component, "D", out var operationId)
                && string.Equals(component, operationId.ToString("D"), StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }

    private static string Digest(Request request, IReadOnlyList<Entry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, request.Kind.ToString());
        Append(hash, request.RootIdentityId);
        Append(hash, request.Key);
        foreach (var entry in entries.OrderBy(value => value.SourceKey, StringComparer.Ordinal))
        {
            Append(hash, entry.SourceKey);
            Append(hash, entry.PinnedObjectIdentity);
            Append(hash, entry.ContentLength.ToString(CultureInfo.InvariantCulture));
            Append(hash, entry.ContentSha256);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    private static PermalinkException Unavailable(string code, string message, Exception? innerException = null)
        => new(PermalinkErrorKind.Unavailable, code, message, innerException);

    /// <summary>Defines one physical tree beneath a registered root identity.</summary>
    /// <param name="Kind">The typed source kind.</param>
    /// <param name="RootIdentityId">The registered root identity.</param>
    /// <param name="SourceRoot">The pinned root against which evidence paths are normalized.</param>
    /// <param name="ScanRoot">The exact subtree to enumerate.</param>
    /// <param name="Key">The stable source key.</param>
    internal sealed record Request(
        SourceKind Kind,
        string RootIdentityId,
        string SourceRoot,
        string ScanRoot,
        string Key);

    /// <summary>One stable source generation and its sorted evidence observations.</summary>
    /// <param name="Generation">The deterministic generation digest.</param>
    /// <param name="Entries">The sorted observations.</param>
    internal sealed record Result(string Generation, IReadOnlyList<Entry> Entries);

    /// <summary>One pinned physical file observation.</summary>
    /// <param name="Kind">The evidence class.</param>
    /// <param name="NormalizedRelativePath">The path relative to the registered root.</param>
    /// <param name="SourceKey">The stable per-source entry key.</param>
    /// <param name="OperationId">The journal operation, when present.</param>
    /// <param name="PinnedObjectIdentity">The opened file descriptor identity.</param>
    /// <param name="ContentLength">The byte length.</param>
    /// <param name="ContentSha256">The lowercase SHA-256.</param>
    internal sealed record Entry(
        EvidenceKind Kind,
        string NormalizedRelativePath,
        string SourceKey,
        string? OperationId,
        string PinnedObjectIdentity,
        long ContentLength,
        string ContentSha256);
}
