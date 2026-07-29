using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Persists deterministic immutable phases for protected permalink mutations.
/// </summary>
internal sealed class PermalinkOperationJournal
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    public PermalinkOperationJournal(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _authority = authority;
        _fileSystem = fileSystem;
    }

    public string GetOperationPath(Guid operationId)
    {
        return Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalinks",
            "operations",
            operationId.ToString("D"));
    }

    public async Task<PermalinkOperationDocument?> ReadAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetOperationPath(operationId), "operation.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return CanonicalJson.Deserialize<PermalinkOperationDocument>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            path);
    }

    public async Task WriteOperationAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var root = GetOperationPath(operation.OperationId);
        _fileSystem.CreateDirectoryDurable(root);
        await PublishExactAsync(
            Path.Combine(root, "operation.json"),
            CanonicalJson.Serialize(operation),
            cancellationToken).ConfigureAwait(false);
    }

    public Task WritePhaseAsync(
        Guid operationId,
        string phase,
        PermalinkOperationPhase document,
        CancellationToken cancellationToken)
    {
        return PublishExactAsync(
            Path.Combine(GetOperationPath(operationId), phase + ".json"),
            CanonicalJson.Serialize(document),
            cancellationToken);
    }

    public bool HasPhase(Guid operationId, string phase)
    {
        return File.Exists(Path.Combine(GetOperationPath(operationId), phase + ".json"));
    }

    public async Task<PermalinkOperationDocument?> FindLatestPendingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        if (!Directory.Exists(root))
        {
            return null;
        }

        PermalinkOperationDocument? latest = null;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationIdText = Path.GetFileName(directory);
            if (!Guid.TryParse(operationIdText, out var operationId)
                || HasPhase(operationId, "committed")
                || HasPhase(operationId, "cancelled")
                || HasPhase(operationId, "assignment_unknown"))
            {
                continue;
            }

            var candidate = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (candidate is not null
                && candidate.ItemId.Equals(itemId)
                && (latest is null
                    || string.CompareOrdinal(candidate.CreatedAt, latest.CreatedAt) > 0))
            {
                latest = candidate;
            }
        }

        return latest;
    }

    /// <summary>Returns whether any non-terminal durable operation fences an item.</summary>
    public async Task<bool> HasPendingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        return await FindLatestPendingAsync(itemId, cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task PublishExactAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            try
            {
                await _fileSystem.PublishImmutableAsync(path, bytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (PermalinkException exception) when (exception.Code == "publish-exclusive")
            {
                // The exact-byte comparison below resolves the concurrent publication.
            }
        }

        var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!existing.AsSpan().SequenceEqual(bytes.Span))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "operation-id-reused",
                $"Operation journal '{path}' already contains different immutable input.");
        }
    }
}

internal sealed record PermalinkOperationDocument(
    Guid OperationId,
    Guid ItemId,
    Guid CapsuleId,
    string Kind,
    string SourcePath,
    string? DestinationPath,
    string OldContentRoot,
    IReadOnlyDictionary<string, string> OldProviderIds,
    IReadOnlyDictionary<string, string> DesiredProviderIds,
    PermalinkIdentitySnapshot? OldIdentity,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-operation";

    public int Version { get; init; } = 1;
}

internal sealed record PermalinkIdentitySnapshot(
    IReadOnlyDictionary<string, string> ProviderIds,
    string ItemKind,
    string? Name,
    string? OriginalTitle,
    int? ProductionYear,
    DateTime? PremiereDate,
    string? SeriesName,
    int? ParentIndexNumber,
    int? IndexNumber,
    int? IndexNumberEnd)
{
    public static PermalinkIdentitySnapshot Capture(BaseItem item)
    {
        return new PermalinkIdentitySnapshot(
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            item.GetType().Name,
            item.Name,
            item.OriginalTitle,
            item.ProductionYear,
            item.PremiereDate,
            item is IHasSeries hasSeries ? hasSeries.SeriesName : null,
            item.ParentIndexNumber,
            item.IndexNumber,
            item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? episode.IndexNumberEnd
                : null);
    }

    public bool Matches(BaseItem item)
    {
        return string.Equals(ItemKind, item.GetType().Name, StringComparison.Ordinal)
            && string.Equals(Name, item.Name, StringComparison.Ordinal)
            && string.Equals(OriginalTitle, item.OriginalTitle, StringComparison.Ordinal)
            && ProductionYear == item.ProductionYear
            && PremiereDate == item.PremiereDate
            && string.Equals(
                SeriesName,
                item is IHasSeries hasSeries ? hasSeries.SeriesName : null,
                StringComparison.Ordinal)
            && ParentIndexNumber == item.ParentIndexNumber
            && IndexNumber == item.IndexNumber
            && IndexNumberEnd == (item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? episode.IndexNumberEnd
                : null)
            && ProviderIds.Count == item.ProviderIds.Count
            && ProviderIds.All(pair => item.ProviderIds.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    public void Restore(BaseItem item)
    {
        item.ProviderIds = new Dictionary<string, string>(ProviderIds, StringComparer.OrdinalIgnoreCase);
        item.Name = Name;
        item.OriginalTitle = OriginalTitle;
        item.ProductionYear = ProductionYear;
        item.PremiereDate = PremiereDate;
        item.ParentIndexNumber = ParentIndexNumber;
        item.IndexNumber = IndexNumber;
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            episode.IndexNumberEnd = IndexNumberEnd;
        }

        if (item is IHasSeries hasSeries)
        {
            hasSeries.SeriesName = SeriesName;
        }
    }
}

internal sealed record PermalinkOperationPhase(
    Guid OperationId,
    string State,
    string? ContentRoot,
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-operation-phase";

    public int Version { get; init; } = 1;
}
