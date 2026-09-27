// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: hostile inputs are exercised by private PermalinkRemovalSafetyTests.cs

/// <summary>Freezes and revalidates exact filesystem and media identity for a removal.</summary>
internal sealed class PermalinkRemovalEvidenceVerifier
{
    private readonly ILibraryManager _libraryManager;
    private readonly PermalinkEvidence _evidence;
    private readonly PermalinkBindingIndex _bindings;
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    public PermalinkRemovalEvidenceVerifier(
        ILibraryManager libraryManager,
        PermalinkEvidence evidence,
        PermalinkBindingIndex bindings,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _evidence = evidence;
        _bindings = bindings;
        _fileSystem = fileSystem;
    }

    public async Task<PermalinkRemovalIntent> FreezeAsync(
        BaseItem source,
        PermalinkRemovalPrepareRequest request,
        CancellationToken cancellationToken)
    {
        PermalinkRemovalPolicy.ValidateInput(source, request);
        if (source is not Video { AdditionalParts.Length: 0 })
        {
            throw Conflict("removal-content-shape", "Removal supports one exact video file.");
        }

        var sourceBindings = RequireUnambiguousBindings(
            source,
            await _bindings.FindItemBindingsAsync(source.Id, cancellationToken).ConfigureAwait(false));
        var sourceBinding = sourceBindings[0];
        var sourceState = GetPathState(source.Path);
        var destinationState = GetPathState(request.DestinationPath);
        var sourceCompanionEntries = EnumerateCompanionEntries(source.Path);
        var destinationCompanionEntries = EnumerateCompanionEntries(request.DestinationPath);
        var attachedAtDestination = sourceState == PermalinkRemovalPathState.Missing
            && sourceCompanionEntries.Count == 0
            && destinationState == PermalinkRemovalPathState.Regular;
        var ordinaryPrepare = sourceState == PermalinkRemovalPathState.Regular
            && destinationState == PermalinkRemovalPathState.Missing
            && destinationCompanionEntries.Count == 0;
        if (!ordinaryPrepare && !attachedAtDestination)
        {
            throw Conflict(
                "removal-file-state",
                "Prepare requires either a complete source with an empty destination, "
                + "or an exact already-moved destination with an empty source.");
        }

        var frozenPath = attachedAtDestination ? request.DestinationPath : source.Path;
        var frozenCompanionEntries = attachedAtDestination
            ? destinationCompanionEntries
            : sourceCompanionEntries;
        var content = await _evidence.ComputeMediaReplacementAsync(source, frozenPath, cancellationToken)
            .ConfigureAwait(false);
        var objectIdentity = RequireObjectIdentity(frozenPath);
        if (!string.Equals(content.ContentRoot, sourceBinding.ContentRoot, StringComparison.Ordinal)
            || !string.Equals(
                _fileSystem.ReadAnchorToken(frozenPath),
                sourceBinding.AnchorToken,
                StringComparison.Ordinal))
        {
            throw Conflict("removal-source-mismatch", "Source bytes or anchor do not match its binding.");
        }

        var companions = await FreezeCompanionsAsync(
            frozenCompanionEntries,
            cancellationToken).ConfigureAwait(false);

        var sourceProviderIdentity = request.SurvivorItemId.HasValue
            ? RequireProviderIdentity(source)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Guid? survivorCapsuleId = null;
        string? survivorContentRoot = null;
        if (request.SurvivorItemId is { } survivorId)
        {
            var survivorBinding = await VerifySurvivorAsync(
                source,
                sourceProviderIdentity,
                survivorId,
                request.EquivalenceEvidence.SurvivorPath,
                cancellationToken).ConfigureAwait(false);
            survivorCapsuleId = survivorBinding.CapsuleId;
            survivorContentRoot = survivorBinding.ContentRoot;
        }

        return new PermalinkRemovalIntent(
            sourceBinding.CapsuleId,
            sourceBinding.ContentRoot,
            request.SurvivorItemId,
            survivorCapsuleId,
            survivorContentRoot,
            request.EquivalenceEvidence.SurvivorPath,
            sourceBinding.AnchorToken,
            objectIdentity.Device,
            objectIdentity.Inode,
            objectIdentity.Length,
            attachedAtDestination,
            companions,
            sourceProviderIdentity,
            sourceBindings.Select(value => value.PermalinkId).ToArray(),
            request.EquivalenceEvidence);
    }

    public async Task VerifyCommitAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var intent = operation.Removal!;
        var sourceState = await ClassifyGroupAsync(
            operation,
            operation.SourcePath,
            cancellationToken).ConfigureAwait(false);
        var destinationState = await ClassifyGroupAsync(
            operation,
            operation.DestinationPath!,
            cancellationToken).ConfigureAwait(false);
        if (sourceState == PermalinkRemovalGroupState.Empty
            && destinationState == PermalinkRemovalGroupState.Ambiguous
            && GetPathState(operation.DestinationPath!) == PermalinkRemovalPathState.Regular)
        {
            await ThrowPreciseDestinationMismatchAsync(operation, cancellationToken)
                .ConfigureAwait(false);
        }

        if (sourceState != PermalinkRemovalGroupState.Empty
            || destinationState != PermalinkRemovalGroupState.Complete)
        {
            throw Conflict(
                "removal-file-state",
                "Commit requires the complete frozen group at the destination and an empty source.");
        }

        if (intent.SurvivorItemId is not { } survivorId)
        {
            return;
        }

        var snapshot = operation.OldIdentity
            ?? throw Conflict("removal-source-identity", "Removal has no frozen source identity.");
        var survivorBinding = await VerifySurvivorAsync(
            snapshot,
            intent.SourceProviderIdentity,
            survivorId,
            intent.SurvivorPath,
            cancellationToken).ConfigureAwait(false);
        if (!intent.SurvivorCapsuleId.HasValue
            || !survivorBinding.CapsuleId.Equals(intent.SurvivorCapsuleId.Value)
            || !string.Equals(
                survivorBinding.ContentRoot,
                intent.SurvivorContentRoot,
                StringComparison.Ordinal))
        {
            throw Conflict("removal-survivor-changed", "The prepared survivor binding changed.");
        }
    }

    private async Task ThrowPreciseDestinationMismatchAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var destination = operation.DestinationPath!;
        var intent = operation.Removal!;
        var identity = RequireObjectIdentity(destination);
        if (identity.Device != intent.SourceDevice
            || identity.Inode != intent.SourceInode
            || identity.Length != intent.SourceLength)
        {
            throw Conflict(
                "removal-object-identity",
                "Destination is not the exact filesystem object prepared at the source path.");
        }

        var content = await _evidence.ComputeSingleFileAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(content.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal)
            || !string.Equals(
                _fileSystem.ReadAnchorToken(destination),
                intent.SourceAnchorToken,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "removal-destination-substituted",
                "Destination bytes or anchor do not match the exact prepared source.");
        }
    }

    /// <summary>Classifies one side of a prepared move using the full frozen group.</summary>
    /// <param name="operation">The prepared removal operation.</param>
    /// <param name="mainPath">The main media path on the side being classified.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fail-closed state of the complete frozen group.</returns>
    public async Task<PermalinkRemovalGroupState> ClassifyGroupAsync(
        PermalinkOperationDocument operation,
        string mainPath,
        CancellationToken cancellationToken)
    {
        var intent = operation.Removal!;
        var entries = EnumerateCompanionEntries(mainPath);
        var mainState = GetPathState(mainPath);
        if (mainState == PermalinkRemovalPathState.Missing && entries.Count == 0)
        {
            return PermalinkRemovalGroupState.Empty;
        }

        if (mainState != PermalinkRemovalPathState.Regular
            || entries.Count != intent.Companions.Count)
        {
            return PermalinkRemovalGroupState.Ambiguous;
        }

        var expectedNames = intent.Companions
            .Select(value => value.FileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var actualNames = entries
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            return PermalinkRemovalGroupState.Ambiguous;
        }

        var identity = MacPermalinkContentIdentity.TryRead(mainPath);
        if (identity is null
            || identity.Value.Device != intent.SourceDevice
            || identity.Value.Inode != intent.SourceInode
            || identity.Value.Length != intent.SourceLength)
        {
            return PermalinkRemovalGroupState.Ambiguous;
        }

        var content = await _evidence.ComputeSingleFileAsync(mainPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(content.ContentRoot, operation.OldContentRoot, StringComparison.Ordinal)
            || !string.Equals(
                _fileSystem.ReadAnchorToken(mainPath),
                intent.SourceAnchorToken,
                StringComparison.Ordinal))
        {
            return PermalinkRemovalGroupState.Ambiguous;
        }

        foreach (var companion in intent.Companions)
        {
            var path = Path.Combine(Path.GetDirectoryName(mainPath)!, companion.FileName);
            if (GetPathState(path) != PermalinkRemovalPathState.Regular
                || new FileInfo(path).Length != companion.Length)
            {
                return PermalinkRemovalGroupState.Ambiguous;
            }

            var companionContent = await _evidence.ComputeSingleFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    companionContent.ContentRoot,
                    companion.ContentRoot,
                    StringComparison.Ordinal))
            {
                return PermalinkRemovalGroupState.Ambiguous;
            }
        }

        return PermalinkRemovalGroupState.Complete;
    }

    private async Task<IReadOnlyList<PermalinkRemovalCompanion>> FreezeCompanionsAsync(
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken)
    {
        var companions = new List<PermalinkRemovalCompanion>(entries.Count);
        foreach (var path in entries)
        {
            if (GetPathState(path) != PermalinkRemovalPathState.Regular)
            {
                throw Conflict(
                    "removal-companion-state",
                    "Every stem-sharing companion must be an exact regular file.");
            }

            var content = await _evidence.ComputeSingleFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            companions.Add(new PermalinkRemovalCompanion(
                Path.GetFileName(path),
                content.ContentRoot,
                new FileInfo(path).Length));
        }

        return companions;
    }

    private async Task<PermalinkResolutionBinding> VerifySurvivorAsync(
        BaseItem source,
        IReadOnlyDictionary<string, string> providerIdentity,
        Guid survivorId,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        return await VerifySurvivorAsync(
            PermalinkIdentitySnapshot.Capture(source),
            providerIdentity,
            survivorId,
            expectedPath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PermalinkResolutionBinding> VerifySurvivorAsync(
        PermalinkIdentitySnapshot source,
        IReadOnlyDictionary<string, string> providerIdentity,
        Guid survivorId,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var survivor = _libraryManager.GetItemById<BaseItem>(survivorId)
            ?? throw Conflict("removal-survivor-missing", "The requested survivor does not exist.");
        var survivorProviderIdentity = RequireProviderIdentity(survivor);
        if (!PermalinkRemovalPolicy.IsEquivalentIdentity(
                source,
                providerIdentity,
                survivor,
                survivorProviderIdentity)
            || !string.Equals(survivor.Path, expectedPath, StringComparison.Ordinal)
            || GetPathState(survivor.Path) != PermalinkRemovalPathState.Regular)
        {
            throw Conflict(
                "removal-survivor-identity",
                "The survivor does not match the authoritative provider and episode identity.");
        }

        var bindings = RequireUnambiguousBindings(
            survivor,
            await _bindings.FindItemBindingsAsync(survivor.Id, cancellationToken).ConfigureAwait(false));
        var binding = bindings[0];
        var content = await _evidence.ComputeContentItemAsync(survivor, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(content.ContentRoot, binding.ContentRoot, StringComparison.Ordinal)
            || !string.Equals(
                _fileSystem.ReadAnchorToken(survivor.Path),
                binding.AnchorToken,
                StringComparison.Ordinal))
        {
            throw Conflict("removal-survivor-capsule", "Survivor does not match its verified capsule.");
        }

        return binding;
    }

    private IReadOnlyDictionary<string, string> RequireProviderIdentity(BaseItem source)
    {
        BaseItem identityOwner = source;
        if (source is Episode episode)
        {
            identityOwner = _libraryManager.GetItemById<Series>(episode.SeriesId)
                ?? throw Conflict("removal-series-missing", "Source episode has no authoritative series.");
        }

        return PermalinkRemovalPolicy.RequireProviderIdentity(identityOwner);
    }

    private static IReadOnlyList<PermalinkResolutionBinding> RequireUnambiguousBindings(
        BaseItem item,
        IReadOnlyList<PermalinkResolutionBinding> bindings)
    {
        if (bindings.Count == 0
            || bindings.Select(value => value.PermalinkId).Distinct(StringComparer.Ordinal).Count() != bindings.Count
            || bindings.Any(value => !value.ItemId.Equals(item.Id))
            || bindings.Select(value => value.CapsuleId).Distinct().Count() != 1
            || bindings.Select(value => value.AnchorToken).Distinct(StringComparer.Ordinal).Count() != 1
            || bindings.Select(value => value.ContentRoot).Distinct(StringComparer.Ordinal).Count() != 1
            || bindings.Any(value => !string.Equals(value.CurrentPath, item.Path, StringComparison.Ordinal)))
        {
            throw Conflict(
                "removal-binding-ambiguous",
                $"Item '{item.Id}' does not have one unambiguous capsule and alias set.");
        }

        return bindings.OrderBy(value => value.PermalinkId, StringComparer.Ordinal).ToArray();
    }

    private static MacPermalinkContentIdentityToken RequireObjectIdentity(string path)
    {
        return MacPermalinkContentIdentity.TryRead(path)
            ?? throw Conflict("removal-object-identity", "Filesystem object identity is unavailable.");
    }

    private static IReadOnlyList<string> EnumerateCompanionEntries(string mainPath)
    {
        var directory = Path.GetDirectoryName(mainPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var prefix = Path.GetFileNameWithoutExtension(mainPath) + ".";
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Where(path => !string.Equals(path, mainPath, StringComparison.Ordinal)
                    && Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException exception)
        {
            throw Conflict("removal-companion-state", $"Cannot enumerate companions: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Conflict("removal-companion-state", $"Cannot enumerate companions: {exception.Message}");
        }
    }

    internal static PermalinkRemovalPathState GetPathState(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? PermalinkRemovalPathState.Regular
                : PermalinkRemovalPathState.Occupied;
        }
        catch (FileNotFoundException)
        {
            return HasLinkTarget(path)
                ? PermalinkRemovalPathState.Occupied
                : PermalinkRemovalPathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return HasLinkTarget(path)
                ? PermalinkRemovalPathState.Occupied
                : PermalinkRemovalPathState.Missing;
        }
        catch (IOException)
        {
            return PermalinkRemovalPathState.Occupied;
        }
        catch (UnauthorizedAccessException)
        {
            return PermalinkRemovalPathState.Occupied;
        }
    }

    private static bool HasLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
