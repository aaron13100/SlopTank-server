using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Publishes and resumes same-root, cross-root, tree, and multipart path bundles.
/// </summary>
internal sealed class PermalinkPathMutation
{
    private readonly ILibraryManager _libraryManager;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkOperationJournal _journal;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkMutationClaimStore _claims;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkCrossRootMutation _crossRoot;

    public PermalinkPathMutation(
        ILibraryManager libraryManager,
        PermalinkEvidence evidence,
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        PermalinkMutationClaimStore claims,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkCrossRootMutation crossRoot)
    {
        _libraryManager = libraryManager;
        _evidence = evidence;
        _journal = journal;
        _bindings = bindings;
        _claims = claims;
        _fileSystem = fileSystem;
        _crossRoot = crossRoot;
    }

    /// <summary>Commits the exact immutable path map without replacing any destination.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationResult> CommitAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var bundle = RequireBundle(operation);
        ValidateDestinations(bundle);
        await VerifyOldAsync(operation, item, cancellationToken).ConfigureAwait(false);
        await _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.Ready,
            new PermalinkOperationPhase(
                operation.OperationId,
                PermalinkPhase.Ready.Name,
                operation.OldContentRoot,
                operation.CreatedAt),
            cancellationToken).ConfigureAwait(false);
        await _claims.ClaimAsync(bundle, operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        await VerifyOldAsync(operation, item, cancellationToken).ConfigureAwait(false);

        if (IsCrossRoot(bundle))
        {
            await _crossRoot.PublishAsync(operation, bundle, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            PublishSameRoot(bundle);
        }

        return await CompleteAsync(operation, item, bundle, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Resumes an interrupted path bundle only from its exact immutable plan.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationResult> RecoverAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var bundle = RequireBundle(operation);
        var allSources = bundle.Parts.All(part =>
            File.Exists(part.SourcePath) || Directory.Exists(part.SourcePath));
        var allDestinations = bundle.Parts.All(part =>
            part.DestinationPath is not null
            && (File.Exists(part.DestinationPath) || Directory.Exists(part.DestinationPath)));
        var anyDestinations = bundle.Parts.Any(part =>
            part.DestinationPath is not null
            && (File.Exists(part.DestinationPath) || Directory.Exists(part.DestinationPath)));
        if (!IsCrossRoot(bundle))
        {
            if (allSources && !anyDestinations)
            {
                return await CommitAsync(operation, item, cancellationToken).ConfigureAwait(false);
            }

            if (allDestinations && !allSources)
            {
                await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                return await CompleteAsync(operation, item, bundle, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteManualInterventionAsync(operation, cancellationToken).ConfigureAwait(false);
            throw Conflict(
                "manual-intervention",
                "Same-root path evidence is mixed and cannot be recovered automatically.");
        }

        if (allSources && !anyDestinations)
        {
            await _claims.ClaimAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }

        await _crossRoot.PublishAsync(operation, bundle, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(operation, item, bundle, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> CompleteAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        var main = bundle.Parts.FirstOrDefault(part => part.Role == "main");
        var itemDestination = main?.DestinationPath ?? operation.DestinationPath;
        if (itemDestination is null)
        {
            throw Conflict("destination-missing", "The prepared path bundle has no item destination.");
        }

        item.Path = itemDestination;
        if (item is Video video)
        {
            video.AdditionalParts = bundle.Parts
                .Where(part => part.Role.StartsWith("additional:", StringComparison.Ordinal))
                .Select(part => part.DestinationPath!)
                .ToArray();
        }

        await _libraryManager.UpdateItemAsync(
            item,
            item.GetParent()!,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);
        var anchor = _fileSystem.ReadAnchorToken(item.Path)
            ?? throw Conflict("anchor-missing", "Moved item lost its stable anchor.");
        await _bindings.UpdateCurrentPathAsync(anchor, item.Path, cancellationToken)
            .ConfigureAwait(false);
        await _claims.FinalizeAsync(
            bundle,
            operation.OperationId,
            "moved",
            cancellationToken).ConfigureAwait(false);
        await WriteTerminalPhasesAsync(operation, cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
    }

    private static void PublishSameRoot(PermalinkMutationBundle bundle)
    {
        var directoryMove = bundle.Parts.Count > 0
            && bundle.Parts.All(part => part.Role.StartsWith("tree:", StringComparison.Ordinal));
        if (directoryMove)
        {
            var first = bundle.Parts[0];
            var relative = first.Role["tree:".Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var sourceRoot = RemoveRelativePath(first.SourcePath, relative);
            var destinationRoot = RemoveRelativePath(first.DestinationPath!, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
            Directory.Move(sourceRoot, destinationRoot);
            return;
        }

        foreach (var part in bundle.Parts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part.DestinationPath!)!);
            File.Move(part.SourcePath, part.DestinationPath!);
        }
    }

    private static string RemoveRelativePath(string path, string relative)
    {
        var root = path;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            _ = segment;
            root = Path.GetDirectoryName(root)
                ?? throw Conflict(
                    "tree-plan-invalid",
                    $"Tree part '{path}' is shorter than relative path '{relative}'.");
        }

        return root;
    }

    private async Task VerifyOldAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var current = await _evidence.ComputeContentItemAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal))
        {
            throw Conflict(
                "prepared-old-mismatch",
                "Live content no longer matches prepared-old path evidence.");
        }
    }

    private async Task RequireClaimsAsync(
        PermalinkMutationBundle bundle,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!await _claims.OwnsAsync(bundle, operationId, cancellationToken).ConfigureAwait(false))
        {
            throw Conflict(
                "claim-owner-mismatch",
                $"Operation '{operationId}' does not own its complete path bundle.");
        }
    }

    private Task WriteManualInterventionAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        return _journal.WritePhaseAsync(
            operation.OperationId,
            PermalinkPhase.ManualIntervention,
            new PermalinkOperationPhase(
                operation.OperationId,
                PermalinkPhase.ManualIntervention.Name,
                null,
                operation.CreatedAt),
            cancellationToken);
    }

    private static bool IsCrossRoot(PermalinkMutationBundle bundle)
    {
        return bundle.DestinationRootPath is not null
            && !string.Equals(
                bundle.ContentRootPath,
                bundle.DestinationRootPath,
                StringComparison.Ordinal);
    }

    private static void ValidateDestinations(PermalinkMutationBundle bundle)
    {
        if (bundle.Parts.Count == 0
            || bundle.Parts.Any(part => string.IsNullOrWhiteSpace(part.DestinationPath)))
        {
            throw Conflict("destination-missing", "The prepared path bundle has no destination.");
        }

        var occupied = bundle.Parts.FirstOrDefault(part =>
            File.Exists(part.DestinationPath) || Directory.Exists(part.DestinationPath));
        if (occupied is not null)
        {
            throw Conflict(
                "destination-occupied",
                $"Prepared destination '{occupied.DestinationPath}' is occupied.");
        }
    }

    private async Task WriteTerminalPhasesAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        foreach (var phase in new[] { PermalinkPhase.Published, PermalinkPhase.Committed })
        {
            await _journal.WritePhaseAsync(
                operation.OperationId,
                phase,
                new PermalinkOperationPhase(
                    operation.OperationId,
                    phase.Name,
                    operation.OldContentRoot,
                    operation.CreatedAt),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static PermalinkMutationBundle RequireBundle(PermalinkOperationDocument operation)
    {
        return operation.Bundle
            ?? throw Conflict("bundle-missing", "Prepared path operation has no immutable bundle.");
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
