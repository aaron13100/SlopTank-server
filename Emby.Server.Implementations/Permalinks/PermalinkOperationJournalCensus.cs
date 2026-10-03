// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Enumerates every hot and archived operation journal byte for the P6 absence watermark.
/// Any malformed, ambiguous, linked, disappearing, or unreadable entry makes absence unknown.
/// </summary>
internal sealed class PermalinkOperationJournalCensus
{
    private readonly string _journalRoot;

    /// <summary>Initializes a new instance of the <see cref="PermalinkOperationJournalCensus"/> class.</summary>
    /// <param name="journalRoot">The directory containing <c>operations</c> and <c>operations-archive</c>.</param>
    public PermalinkOperationJournalCensus(string journalRoot)
    {
        _journalRoot = Path.GetFullPath(journalRoot);
    }

    /// <summary>Reads one complete fail-closed journal census.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The two content-addressed generations and their exact cardinalities.</returns>
    public async Task<PermalinkJournalCensus> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seen = new HashSet<Guid>();
            var hot = await ReadHotAsync(seen, cancellationToken).ConfigureAwait(false);
            var archive = await ReadArchiveAsync(seen, cancellationToken).ConfigureAwait(false);
            return new PermalinkJournalCensus(
                Digest(hot.Files),
                hot.Cardinality,
                Digest(archive.Files),
                archive.Cardinality);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Unavailable(
                "absence-journal-unreadable",
                $"The complete permalink operation journal could not be read ({exception.Message}).",
                exception);
        }
    }

    private async Task<JournalArea> ReadHotAsync(
        HashSet<Guid> seen,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_journalRoot, "operations");
        if (!TryGetAttributes(root, out var rootAttributes))
        {
            return JournalArea.Empty;
        }

        RequireDirectory(root, rootAttributes, "absence-journal-operation-invalid");
        var files = new List<JournalFile>();
        var cardinality = 0;
        foreach (var entry in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(entry, "absence-journal-operation-invalid");
            var operationId = RequireOperationId(entry);
            AddUnique(seen, operationId);
            await ReadOperationAsync(entry, "hot/" + operationId.ToString("D"), operationId, files, cancellationToken)
                .ConfigureAwait(false);
            cardinality++;
        }

        return new JournalArea(files, cardinality);
    }

    private async Task<JournalArea> ReadArchiveAsync(
        HashSet<Guid> seen,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_journalRoot, "operations-archive");
        if (!TryGetAttributes(root, out var rootAttributes))
        {
            return JournalArea.Empty;
        }

        RequireDirectory(root, rootAttributes, "absence-journal-operation-invalid");
        var files = new List<JournalFile>();
        var cardinality = 0;
        foreach (var monthPath in EnumerateEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(monthPath, "absence-journal-archive-month-invalid");
            var month = Path.GetFileName(monthPath);
            if (!DateTime.TryParseExact(
                    month,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)
                || !string.Equals(
                    month,
                    DateTime.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture)
                        .ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                throw Unavailable(
                    "absence-journal-archive-month-invalid",
                    $"Archive entry '{monthPath}' is not a canonical yyyy-MM month directory.");
            }

            foreach (var entry in EnumerateEntries(monthPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireDirectory(entry, "absence-journal-operation-invalid");
                var operationId = RequireOperationId(entry);
                AddUnique(seen, operationId);
                await ReadOperationAsync(
                        entry,
                        "archive/" + month + "/" + operationId.ToString("D"),
                        operationId,
                        files,
                        cancellationToken)
                    .ConfigureAwait(false);
                cardinality++;
            }
        }

        return new JournalArea(files, cardinality);
    }

    private static IEnumerable<string> EnumerateEntries(string root)
        => Directory.EnumerateFileSystemEntries(root)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);

    private static void RequireDirectory(string path, string code)
    {
        RequireDirectory(path, File.GetAttributes(path), code);
    }

    private static void RequireDirectory(string path, FileAttributes attributes, string code)
    {
        RequireUnlinked(path, attributes);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Unavailable(code, $"Journal entry '{path}' is not a directory.");
        }
    }

    private static void RequireUnlinked(string path)
        => RequireUnlinked(path, File.GetAttributes(path));

    private static void RequireUnlinked(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Unavailable(
                "absence-journal-linked",
                $"Journal entry '{path}' is a symbolic link and was refused.");
        }
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

    private static Guid RequireOperationId(string path)
    {
        var name = Path.GetFileName(path);
        if (!Guid.TryParseExact(name, "D", out var operationId)
            || !string.Equals(name, operationId.ToString("D"), StringComparison.Ordinal))
        {
            throw Unavailable(
                "absence-journal-operation-invalid",
                $"Journal entry '{path}' does not have a canonical operation identifier.");
        }

        return operationId;
    }

    private static void AddUnique(HashSet<Guid> seen, Guid operationId)
    {
        if (!seen.Add(operationId))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "absence-journal-operation-duplicate",
                $"Operation '{operationId:D}' appears more than once in the hot/archive journal census.");
        }
    }

    private static async Task ReadOperationAsync(
        string operationPath,
        string generationPrefix,
        Guid operationId,
        List<JournalFile> files,
        CancellationToken cancellationToken)
    {
        string? operationDocument = null;
        foreach (var entry in EnumerateEntries(operationPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            RequireUnlinked(entry, attributes);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw Unavailable(
                    "absence-journal-operation-invalid",
                    $"Operation '{operationId:D}' contains a non-file entry '{entry}'.");
            }

            var name = Path.GetFileName(entry);
            var bytes = await File.ReadAllBytesAsync(entry, cancellationToken).ConfigureAwait(false);
            RequireUnlinked(entry);
            files.Add(new JournalFile(generationPrefix + "/" + name, bytes));
            if (string.Equals(name, "operation.json", StringComparison.Ordinal))
            {
                operationDocument = entry;
                using var document = JsonDocument.Parse(bytes);
                if (!document.RootElement.TryGetProperty("operation_id", out var property)
                    || property.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(property.GetString(), "D", out var documentId)
                    || !documentId.Equals(operationId))
                {
                    throw new PermalinkException(
                        PermalinkErrorKind.Conflict,
                        "absence-journal-operation-mismatch",
                        $"Operation directory '{operationId:D}' contains a mismatched operation document.");
                }
            }
        }

        if (operationDocument is null)
        {
            throw Unavailable(
                "absence-journal-operation-missing",
                $"Operation '{operationId:D}' has no operation.json document.");
        }
    }

    private static string Digest(IReadOnlyList<JournalFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var file in files.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            var path = Encoding.UTF8.GetBytes(file.RelativePath);
            BinaryPrimitives.WriteInt64BigEndian(length, path.Length);
            hash.AppendData(length);
            hash.AppendData(path);
            BinaryPrimitives.WriteInt64BigEndian(length, file.Contents.LongLength);
            hash.AppendData(length);
            hash.AppendData(SHA256.HashData(file.Contents));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static PermalinkException Unavailable(string code, string message, Exception? exception = null)
        => new(PermalinkErrorKind.Unavailable, code, message, exception);

    private sealed record JournalFile(string RelativePath, byte[] Contents);

    private sealed record JournalArea(IReadOnlyList<JournalFile> Files, int Cardinality)
    {
        public static JournalArea Empty { get; } = new(Array.Empty<JournalFile>(), 0);
    }
}
