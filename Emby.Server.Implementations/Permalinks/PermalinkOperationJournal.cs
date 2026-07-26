using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    string CreatedAt)
{
    public string Type { get; init; } = "sloptank.permalink-operation";

    public int Version { get; init; } = 1;
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
