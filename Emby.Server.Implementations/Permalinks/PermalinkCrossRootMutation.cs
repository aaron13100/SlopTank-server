using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Stages, verifies, publishes, and quarantines every part in one cross-root mutation plan.
/// </summary>
internal sealed class PermalinkCrossRootMutation
{
    private readonly PermalinkOperationJournal _journal;
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    public PermalinkCrossRootMutation(
        PermalinkOperationJournal journal,
        IPermalinkAtomicFileSystem fileSystem)
    {
        _journal = journal;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Converges destination publication before quarantining any remaining source part.
    /// </summary>
    /// <param name="operation">The immutable operation document.</param>
    /// <param name="bundle">The immutable mutation bundle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task PublishAsync(
        PermalinkOperationDocument operation,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        if (IsTree(bundle))
        {
            await PublishTreeAsync(operation, bundle, cancellationToken).ConfigureAwait(false);
            return;
        }

        var destinationRoot = bundle.DestinationRootPath
            ?? throw Conflict("destination-root-missing", "Cross-root bundle has no destination root.");
        var destinationStage = Path.Combine(
            destinationRoot,
            ".sloptank",
            "permalink-operations",
            operation.OperationId.ToString("D"),
            "staging");
        var sourceQuarantine = Path.Combine(
            _journal.GetContentOperationPath(operation),
            "quarantine");
        _fileSystem.CreateDirectoryDurable(destinationStage);
        _fileSystem.CreateDirectoryDurable(sourceQuarantine);
        for (var index = 0; index < bundle.Parts.Count; index++)
        {
            var part = bundle.Parts[index];
            var ordinal = index.ToString("D6", CultureInfo.InvariantCulture);
            var staged = Path.Combine(destinationStage, ordinal);
            var destination = part.DestinationPath!;
            var quarantine = Path.Combine(sourceQuarantine, ordinal);
            if (!File.Exists(staged)
                && !File.Exists(destination)
                && File.Exists(part.SourcePath))
            {
                await CopyExclusiveAsync(part.SourcePath, staged, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (part.Role == "main" && File.Exists(staged))
            {
                var anchor = _fileSystem.ReadAnchorToken(part.SourcePath)
                    ?? throw Conflict("anchor-missing", "Cross-root source lost its stable anchor.");
                _fileSystem.AssignAnchorToken(staged, anchor);
            }

            _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                if (!File.Exists(staged))
                {
                    throw Conflict(
                        "cross-root-staging-missing",
                        $"Planned staging for '{part.Role}' is missing.");
                }

                File.Move(staged, destination);
            }

            var reference = File.Exists(part.SourcePath) ? part.SourcePath : quarantine;
            await VerifyEqualAsync(reference, destination, cancellationToken)
                .ConfigureAwait(false);
            await WritePartPhaseAsync(
                operation,
                index,
                "published",
                cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < bundle.Parts.Count; index++)
        {
            var part = bundle.Parts[index];
            var quarantine = Path.Combine(
                sourceQuarantine,
                index.ToString("D6", CultureInfo.InvariantCulture));
            if (File.Exists(part.SourcePath) && !File.Exists(quarantine))
            {
                File.Move(part.SourcePath, quarantine);
            }

            if (!File.Exists(quarantine))
            {
                throw Conflict(
                    "cross-root-quarantine-missing",
                    $"Prepared source evidence for '{part.Role}' is missing.");
            }

            await WritePartPhaseAsync(
                operation,
                index,
                "quarantined",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishTreeAsync(
        PermalinkOperationDocument operation,
        PermalinkMutationBundle bundle,
        CancellationToken cancellationToken)
    {
        var destinationContentRoot = bundle.DestinationRootPath
            ?? throw Conflict("destination-root-missing", "Cross-root bundle has no destination root.");
        var first = bundle.Parts[0];
        var firstRelative = GetTreeRelativePath(first);
        var sourceRoot = RemoveRelativePath(first.SourcePath, firstRelative);
        var destinationRoot = RemoveRelativePath(first.DestinationPath!, firstRelative);
        var stagingRoot = Path.Combine(
            destinationContentRoot,
            ".sloptank",
            "permalink-operations",
            operation.OperationId.ToString("D"),
            "staging-tree");
        var quarantineRoot = Path.Combine(
            _journal.GetContentOperationPath(operation),
            "quarantine-tree");

        var sourceExists = Directory.Exists(sourceRoot);
        var quarantineExists = Directory.Exists(quarantineRoot);
        if (sourceExists && quarantineExists)
        {
            throw Conflict(
                "cross-root-tree-source-mixed",
                "Cross-root tree exists at both its source and quarantine paths.");
        }

        var referenceRoot = sourceExists
            ? sourceRoot
            : quarantineExists
                ? quarantineRoot
                : null;
        if (!Directory.Exists(destinationRoot))
        {
            if (referenceRoot is null)
            {
                throw Conflict(
                    "cross-root-tree-evidence-missing",
                    "Cross-root tree has no complete source or quarantine evidence.");
            }

            _fileSystem.CreateDirectoryDurable(stagingRoot);
            foreach (var part in bundle.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = GetTreeRelativePath(part);
                var reference = Path.Combine(referenceRoot, relative);
                var staged = Path.Combine(stagingRoot, relative);
                _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(staged)!);
                if (!File.Exists(staged))
                {
                    await CopyExclusiveAsync(reference, staged, cancellationToken)
                        .ConfigureAwait(false);
                }

                await VerifyEqualAsync(reference, staged, cancellationToken)
                    .ConfigureAwait(false);
            }

            var anchor = _fileSystem.ReadAnchorToken(referenceRoot)
                ?? throw Conflict(
                    "anchor-missing",
                    "Cross-root tree source lost its stable anchor.");
            _fileSystem.AssignAnchorToken(stagingRoot, anchor);
            _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(destinationRoot)!);
            _fileSystem.PublishDirectoryImmutable(stagingRoot, destinationRoot);
        }

        if (referenceRoot is null)
        {
            throw Conflict(
                "cross-root-tree-evidence-missing",
                "Published cross-root tree has no source or quarantine evidence.");
        }

        for (var index = 0; index < bundle.Parts.Count; index++)
        {
            var part = bundle.Parts[index];
            var relative = GetTreeRelativePath(part);
            await VerifyEqualAsync(
                Path.Combine(referenceRoot, relative),
                part.DestinationPath!,
                cancellationToken).ConfigureAwait(false);
            await WritePartPhaseAsync(operation, index, "published", cancellationToken)
                .ConfigureAwait(false);
        }

        if (sourceExists)
        {
            _fileSystem.CreateDirectoryDurable(Path.GetDirectoryName(quarantineRoot)!);
            Directory.Move(sourceRoot, quarantineRoot);
        }

        if (!Directory.Exists(quarantineRoot))
        {
            throw Conflict(
                "cross-root-tree-quarantine-missing",
                "Prepared cross-root tree quarantine evidence is missing.");
        }

        for (var index = 0; index < bundle.Parts.Count; index++)
        {
            await WritePartPhaseAsync(operation, index, "quarantined", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task WritePartPhaseAsync(
        PermalinkOperationDocument operation,
        int index,
        string state,
        CancellationToken cancellationToken)
    {
        var phase = $"part-{index}-{state}";
        return _journal.WritePhaseAsync(
            operation.OperationId,
            phase,
            new PermalinkOperationPhase(
                operation.OperationId,
                phase,
                operation.OldContentRoot,
                operation.CreatedAt),
            cancellationToken);
    }

    private static async Task VerifyEqualAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source) || !File.Exists(destination))
        {
            throw Conflict(
                "cross-root-copy-missing",
                $"Cross-root evidence is missing at '{source}' or '{destination}'.");
        }

        await using var sourceStream = OpenRead(source);
        await using var destinationStream = OpenRead(destination);
        var sourceDigest = await SHA256.HashDataAsync(sourceStream, cancellationToken)
            .ConfigureAwait(false);
        var destinationDigest = await SHA256.HashDataAsync(
            destinationStream,
            cancellationToken).ConfigureAwait(false);
        if (sourceStream.Length != destinationStream.Length
            || !sourceDigest.AsSpan().SequenceEqual(destinationDigest))
        {
            throw Conflict(
                "cross-root-copy-mismatch",
                $"Cross-root destination '{destination}' does not match '{source}'.");
        }
    }

    private async Task CopyExclusiveAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = OpenRead(source))
        await using (var output = new FileStream(
                         destination,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _fileSystem.SyncFile(destination);
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static bool IsTree(PermalinkMutationBundle bundle)
    {
        return bundle.Parts.Count > 0
            && bundle.Parts.All(part => part.Role.StartsWith("tree:", StringComparison.Ordinal));
    }

    private static string GetTreeRelativePath(PermalinkMutationPart part)
    {
        return part.Role["tree:".Length..].Replace('/', Path.DirectorySeparatorChar);
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

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
