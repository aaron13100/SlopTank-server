// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-02, 2026-09-09, 2026-10-03, 2026-10-08, 2026-10-09.
using System;
using System.Collections.Generic;
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
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly PermalinkCrossRootMutation _crossRoot;
    private readonly PermalinkPathLeaseFactory _leases;
    private readonly PermalinkAdoptedGenerationProber _generationProber;

    public PermalinkPathMutation(
        ILibraryManager libraryManager,
        PermalinkEvidence evidence,
        PermalinkOperationJournal journal,
        PermalinkBindingIndex bindings,
        PermalinkMutationClaimStore claims,
        PermalinkAuthorityStore authority,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkCrossRootMutation crossRoot,
        PermalinkPathLeaseFactory leases,
        PermalinkAdoptedGenerationProber generationProber)
    {
        _libraryManager = libraryManager;
        _evidence = evidence;
        _journal = journal;
        _bindings = bindings;
        _claims = claims;
        _authority = authority;
        _capsules = capsules;
        _documents = documents;
        _fileSystem = fileSystem;
        _crossRoot = crossRoot;
        _leases = leases;
        _generationProber = generationProber;
    }

    /// <summary>Commits the exact immutable path map without replacing any destination.</summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="item">The library item.</param>
    /// <param name="writerAdmissionId">The external writer admission, when this commit owns one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkMutationResult> CommitAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        Guid? writerAdmissionId,
        CancellationToken cancellationToken)
    {
        var bundle = RequireBundle(operation);
        ValidateDestinations(bundle);
        var destinationParent = await ResolveDestinationParentAsync(
            operation,
            item,
            bundle,
            cancellationToken).ConfigureAwait(false);
        await using (var leases = await AcquireAsync(bundle, cancellationToken).ConfigureAwait(false))
        {
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
            var permit = writerAdmissionId.HasValue
                ? await leases.RedeemAdmissionAsync(
                    operation.SourcePath,
                    writerAdmissionId.Value,
                    "replace-path",
                    cancellationToken).ConfigureAwait(false)
                : null;

            if (IsCrossRoot(bundle))
            {
                await _crossRoot.PublishAsync(operation, bundle, leases, cancellationToken, permit)
                    .ConfigureAwait(false);
            }
            else
            {
                await PublishSameRootAsync(leases, bundle, cancellationToken, permit).ConfigureAwait(false);
            }
        }

        return await CompleteAsync(operation, item, bundle, destinationParent, cancellationToken)
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
        var destinationParent = await ResolveDestinationParentAsync(
            operation,
            item,
            bundle,
            cancellationToken).ConfigureAwait(false);
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
                return await CommitAsync(operation, item, writerAdmissionId: null, cancellationToken).ConfigureAwait(false);
            }

            if (allDestinations && !allSources)
            {
                await RequireClaimsAsync(bundle, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                return await CompleteAsync(operation, item, bundle, destinationParent, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteManualInterventionAsync(operation, cancellationToken).ConfigureAwait(false);
            throw Conflict(
                "manual-intervention",
                "Same-root path evidence is mixed and cannot be recovered automatically.");
        }

        await using (var leases = await AcquireAsync(bundle, cancellationToken).ConfigureAwait(false))
        {
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

            await _crossRoot.PublishAsync(operation, bundle, leases, cancellationToken).ConfigureAwait(false);
        }

        return await CompleteAsync(operation, item, bundle, destinationParent, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PermalinkMutationResult> CompleteAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationBundle bundle,
        Folder destinationParent,
        CancellationToken cancellationToken)
    {
        var itemDestination = RequireItemDestination(operation, bundle);
        var sourceParent = item.GetParent() as Folder;

        item.Path = itemDestination;
        if (item is Video video)
        {
            video.AdditionalParts = bundle.Parts
                .Where(part => part.Role.StartsWith("additional:", StringComparison.Ordinal))
                .Select(part => part.DestinationPath!)
                .ToArray();
        }

        await ReprobeCurrentGenerationAsync(item, cancellationToken).ConfigureAwait(false);
        item.SetParent(destinationParent);
        await _libraryManager.UpdateItemAsync(
            item,
            destinationParent,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);
        if (sourceParent is not null && !sourceParent.Id.Equals(destinationParent.Id))
        {
            sourceParent.Children = null;
        }

        // The commit repointed the row to a new physical generation: re-register the
        // persisted row so the GetItemById-cached instance (the seam PlaybackInfo and
        // the visible-item paths resolve) serves the same generation it just adopted.
        _generationProber.RefreshCachedItem(item.Id);

        var anchor = _fileSystem.ReadAnchorToken(item.Path)
            ?? throw Conflict("anchor-missing", "Moved item lost its stable anchor.");
        await PublishPathSuccessorsAsync(
            operation,
            bundle,
            itemDestination,
            cancellationToken).ConfigureAwait(false);
        await _bindings.UpdateCurrentPathAsync(anchor, item.Path, cancellationToken)
            .ConfigureAwait(false);
        await WriteTerminalPhasesAsync(operation, cancellationToken).ConfigureAwait(false);
        return new PermalinkMutationResult(operation.OperationId, PermalinkPhase.Committed.Name);
    }

    private async Task PublishPathSuccessorsAsync(
        PermalinkOperationDocument operation,
        PermalinkMutationBundle bundle,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<Guid, (string CapsulePath, PermalinkEventDocument Event, ReadOnlyMemory<byte> Bytes)>();
        foreach (var claim in bundle.Claims)
        {
            var reservation = await _authority.FindByCapsuleAsync(claim.CapsuleId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict("capsule-missing", $"Protected capsule '{claim.CapsuleId}' is unknown.");
            // Directory capsules travel inside the tree bundle. Once publication has
            // completed, the authority still names the source-era location, so derive
            // the adopted capsule from the now-present destination tree. File capsules
            // remain rooted in the authority store and therefore keep their reservation
            // path even when the media file crosses roots.
            var capsulePath = Directory.Exists(destinationPath)
                ? _capsules.GetCapsulePath(
                    bundle.DestinationRootPath ?? reservation.RootPath,
                    destinationPath,
                    reservation.ItemKind,
                    reservation.CapsuleId)
                : reservation.CapsulePath;
            var snapshot = await _capsules.ReadValidatedAsync(
                capsulePath,
                reservation.CapsuleId,
                reservation.ItemKind,
                cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParseExact(claim.Predecessor, "D", out var predecessor))
            {
                throw Conflict(
                    "path-predecessor-invalid",
                    $"Prepared path predecessor '{claim.Predecessor}' is not an event id.");
            }

            PermalinkEventDocument successor;
            if (snapshot.PathHead.PathAssignment.EventId.Equals(predecessor))
            {
                successor = _documents.CreatePathSuccessor(
                    snapshot,
                    operation.OperationId,
                    claim.BindingInstanceId,
                    destinationPath,
                    bundle.DestinationRootPath ?? reservation.RootPath,
                    operation.CreatedAt);
            }
            else if (snapshot.PathHead.OperationId.Equals(operation.OperationId)
                     && snapshot.PathHead.BindingInstanceId.Equals(claim.BindingInstanceId)
                     && snapshot.PathHead.PathAssignment.PreviousPathEventId.Equals(predecessor)
                     && string.Equals(
                         NormalizePath(snapshot.PathHead.PathAssignment.Path),
                         NormalizePath(destinationPath),
                         StringComparison.OrdinalIgnoreCase))
            {
                successor = snapshot.PathHead;
            }
            else
            {
                throw Conflict(
                    "path-successor-head-mismatch",
                    $"Prepared predecessor for capsule '{claim.CapsuleId}' is no longer current.");
            }

            prepared.Add(
                claim.CapsuleId,
                (capsulePath, successor, CanonicalJson.Serialize(successor)));
        }

        await _claims.FinalizePathAsync(
            bundle,
            operation.OperationId,
            prepared.ToDictionary(entry => entry.Key, entry => entry.Value.Event),
            cancellationToken).ConfigureAwait(false);
        foreach (var successor in prepared.OrderBy(entry => entry.Key).Select(entry => entry.Value))
        {
            await _capsules.AppendPathAsync(
                successor.CapsulePath,
                successor.Event,
                successor.Bytes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the persisted parent that owns the destination slot before any physical
    /// publication begins. A path move changes both coordinates of a library row: its
    /// path and its parent. Persisting only the path leaves the row invisible to the
    /// destination scanner, which then creates a new path-derived identity.
    /// </summary>
    private async Task<Folder> ResolveDestinationParentAsync(
        PermalinkOperationDocument operation,
        BaseItem item,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        var itemDestination = RequireItemDestination(operation, bundle);
        var parentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(itemDestination));
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            throw Conflict(
                "destination-parent-missing",
                $"Prepared destination '{itemDestination}' has no parent directory.");
        }

        var currentParent = item.GetParent();
        if (currentParent is Folder currentFolder
            && string.Equals(
                Path.GetFullPath(currentFolder.Path),
                Path.GetFullPath(parentPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return currentFolder;
        }

        var normalizedParent = NormalizePath(parentPath);
        if (_libraryManager.FindByPath(normalizedParent, isFolder: true) is Folder exactParent
            && string.Equals(
                NormalizePath(exactParent.Path),
                normalizedParent,
                StringComparison.OrdinalIgnoreCase))
        {
            return exactParent;
        }

        var collections = _libraryManager.GetUserRootFolder().Children
            .OfType<CollectionFolder>()
            .ToArray();
        var registeredRoots = collections
            .SelectMany(collection => collection.PhysicalFolderIds
                .Select(_libraryManager.GetItemById)
                .OfType<Folder>()
                .Select(folder => (
                    Collection: collection,
                    Path: folder.Path,
                    Folder: (Folder?)folder)));
        var configuredRoots = collections
            .SelectMany(collection => collection.PhysicalLocations
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => (
                    Collection: collection,
                    Path: path,
                    Folder: (Folder?)null)));
        var root = registeredRoots
            .Concat(configuredRoots)
            .Where(candidate => IsWithinPath(candidate.Path, normalizedParent))
            .OrderByDescending(candidate => NormalizePath(candidate.Path).Length)
            .ThenBy(candidate => candidate.Folder is null)
            .ThenBy(candidate => candidate.Collection.Id)
            .FirstOrDefault();
        if (root.Collection is null)
        {
            throw Conflict(
                "destination-parent-unindexed",
                $"Prepared destination parent '{parentPath}' is outside every indexed physical library root.");
        }

        var physicalRoot = root.Folder
            ?? _libraryManager.FindByPath(root.Path, isFolder: true) as Folder;
        if (physicalRoot is null)
        {
            if (!Directory.Exists(root.Path))
            {
                throw Conflict(
                    "destination-parent-unindexed",
                    $"Configured library root '{root.Path}' is unavailable for destination '{parentPath}'.");
            }

            var rootFolder = _libraryManager.RootFolder;
            var physicalRootId = _libraryManager.GetNewItemId(root.Path, typeof(Folder));
            physicalRoot = _libraryManager.GetItemById<Folder>(physicalRootId);
            if (physicalRoot is null)
            {
                physicalRoot = new Folder
                {
                    Id = physicalRootId,
                    Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root.Path)),
                    Path = root.Path
                };
                physicalRoot.SetParent(rootFolder);
                _libraryManager.CreateItem(physicalRoot, rootFolder);
            }
        }

        if (!root.Collection.PhysicalFolderIds.Contains(physicalRoot.Id))
        {
            root.Collection.PhysicalFolderIds = root.Collection.PhysicalFolderIds
                .Append(physicalRoot.Id)
                .Distinct()
                .ToArray();
            await _libraryManager.UpdateItemAsync(
                root.Collection,
                root.Collection.GetParent()!,
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
        }

        var normalizedRoot = NormalizePath(physicalRoot.Path);
        for (var candidatePath = normalizedParent;
             IsWithinPath(normalizedRoot, candidatePath);
             candidatePath = Path.GetDirectoryName(candidatePath) ?? string.Empty)
        {
            if (string.Equals(candidatePath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return physicalRoot;
            }

            if (_libraryManager.FindByPath(candidatePath, isFolder: true) is Folder indexed
                && string.Equals(
                    NormalizePath(indexed.Path),
                    candidatePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return indexed;
            }
        }

        throw Conflict(
            "destination-parent-unindexed",
            $"Prepared destination parent '{parentPath}' has no indexed library ancestor.");
    }

    private static bool IsWithinPath(string rootPath, string candidatePath)
    {
        var root = NormalizePath(rootPath);
        var candidate = NormalizePath(candidatePath);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string RequireItemDestination(
        PermalinkOperationDocument operation,
        PermalinkMutationBundle bundle)
    {
        var main = bundle.Parts.FirstOrDefault(part => part.Role == "main");
        return main?.DestinationPath
            ?? operation.DestinationPath
            ?? throw Conflict(
                "destination-missing",
                "The prepared path bundle has no item destination.");
    }

    /// <summary>
    /// Re-probes the item's media info from the generation just installed at the adopted
    /// destination: the persisted stream rows and every served media view must describe
    /// the destination generation the moment the commit returns, not only after some
    /// later scan refresh. See <see cref="PermalinkAdoptedGenerationProber"/> for the
    /// shared failure contract.
    /// </summary>
    /// <param name="item">The item whose path was just repointed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private Task ReprobeCurrentGenerationAsync(BaseItem item, CancellationToken cancellationToken)
        => _generationProber.ReprobeAsync(item, cancellationToken);

    /// <summary>
    /// Leases every library path a bundle will create, rename or unlink: each part's source and
    /// destination plus, for a tree, its source and destination roots.
    /// </summary>
    private Task<PermalinkWriterLeaseSet> AcquireAsync(
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        return _leases.AcquireWriterSetAsync(LeasedPaths(bundle), cancellationToken);
    }

    private static List<string> LeasedPaths(PermalinkMutationBundle bundle)
    {
        var paths = new List<string>();
        foreach (var part in bundle.Parts)
        {
            paths.Add(part.SourcePath);
            if (!string.IsNullOrWhiteSpace(part.DestinationPath))
            {
                paths.Add(part.DestinationPath);
            }
        }

        if (bundle.Parts.Count > 0
            && bundle.Parts.All(part => part.Role.StartsWith("tree:", StringComparison.Ordinal)))
        {
            var first = bundle.Parts[0];
            var relative = first.Role["tree:".Length..].Replace('/', Path.DirectorySeparatorChar);
            paths.Add(RemoveRelativePath(first.SourcePath, relative));
            if (!string.IsNullOrWhiteSpace(first.DestinationPath))
            {
                paths.Add(RemoveRelativePath(first.DestinationPath, relative));
            }
        }

        return paths;
    }

    /// <summary>
    /// Publishes a same-root bundle by renaming it. Every leased key is rechecked once before the
    /// first rename, so a lost recheck cannot leave a half-moved multipart bundle, and each step
    /// rechecks the keys it touches again immediately before its own rename.
    /// </summary>
    /// <param name="leases">The writer's leases; must cover every part and tree root.</param>
    /// <param name="bundle">The immutable bundle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="permit">The external writer permit already redeemed for the source key, or null.</param>
    /// <returns>A task representing the publication.</returns>
    /// <exception cref="PermalinkException">Conflict when any leased key changed.</exception>
    internal static async Task PublishSameRootAsync(
        PermalinkWriterLeaseSet leases,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken,
        PermalinkWriterAdmissionPermit? permit = null)
    {
        await leases.RecheckAllWithPermitAsync(permit, cancellationToken).ConfigureAwait(false);
        var directoryMove = bundle.Parts.Count > 0
            && bundle.Parts.All(part => part.Role.StartsWith("tree:", StringComparison.Ordinal));
        if (directoryMove)
        {
            var first = bundle.Parts[0];
            var relative = first.Role["tree:".Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var sourceRoot = RemoveRelativePath(first.SourcePath, relative);
            var destinationRoot = RemoveRelativePath(first.DestinationPath!, relative);
            await leases.RecheckWithPermitAsync(sourceRoot, permit, cancellationToken).ConfigureAwait(false);
            await leases.RecheckWithPermitAsync(destinationRoot, permit, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
            Directory.Move(sourceRoot, destinationRoot);
            return;
        }

        foreach (var part in bundle.Parts)
        {
            await leases.RecheckWithPermitAsync(part.SourcePath, permit, cancellationToken).ConfigureAwait(false);
            await leases.RecheckWithPermitAsync(part.DestinationPath!, permit, cancellationToken).ConfigureAwait(false);
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
