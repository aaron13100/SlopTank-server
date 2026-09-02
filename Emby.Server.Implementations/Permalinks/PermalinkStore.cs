using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Coordinates one recoverable authority election with portable capsule publication.
/// </summary>
internal sealed class PermalinkStore : IPermalinkStore
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkTransitionStore _transitions;
    private readonly PermalinkBindingIndex _bindings;
    private readonly PermalinkCapsuleStore _capsules;
    private readonly PermalinkDocumentFactory _documents;
    private readonly PermalinkTransitionPublisher _publisher;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly ILogger<PermalinkStore> _logger;
    private readonly string[] _contentRoots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkStore"/> class.
    /// </summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="transitions">The transitions.</param>
    /// <param name="bindings">The bindings.</param>
    /// <param name="capsules">The capsules.</param>
    /// <param name="documents">The documents.</param>
    /// <param name="publisher">The publisher.</param>
    /// <param name="fileSystem">The durable permalink filesystem.</param>
    /// <param name="logger">The logger, used for the per-step ensure timing.</param>
    /// <param name="configuration">The server configuration.</param>
    public PermalinkStore(
        PermalinkAuthorityStore authority,
        PermalinkTransitionStore transitions,
        PermalinkBindingIndex bindings,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkTransitionPublisher publisher,
        IPermalinkAtomicFileSystem fileSystem,
        ILogger<PermalinkStore> logger,
        IConfiguration configuration)
    {
        _authority = authority;
        _transitions = transitions;
        _bindings = bindings;
        _capsules = capsules;
        _documents = documents;
        _publisher = publisher;
        _fileSystem = fileSystem;
        _logger = logger;
        _contentRoots = configuration.GetSection("Permalinks:ContentRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        CancellationToken cancellationToken)
    {
        // Temporary per-step timing. Seven hypotheses about where an ensure spends
        // its 1.4s were each measured from outside the process and disproven: every
        // primitive it touches is under 2ms, yet three ensures per watch-link open
        // are most of a 13.4s wait. This reads the answer instead of guessing an
        // eighth time. Remove once the hot step is known and fixed.
        var stepWatch = Stopwatch.StartNew();
        var totalWatch = Stopwatch.StartNew();
        long tAvailable, tAnchor, tSamePath, tReservation, tCapsule, tRead, tReconcile, tAliases, tBind;

        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        tAvailable = stepWatch.ElapsedMilliseconds;
        stepWatch.Restart();
        EnsureReachableOrVirtualSeason(request);
        var root = ResolveRoot(request.Path);
        var anchorToken = await _fileSystem.GetOrCreateAnchorTokenAsync(
            request.Path,
            cancellationToken).ConfigureAwait(false);
        tAnchor = stepWatch.ElapsedMilliseconds;
        stepWatch.Restart();

        var samePath = await _authority.FindByCurrentPathAsync(
            request.Path,
            cancellationToken).ConfigureAwait(false);
        if (samePath is not null
            && !string.Equals(samePath.AnchorToken, anchorToken, StringComparison.Ordinal))
        {
            throw Conflict(
                "anchor-replacement",
                $"Path '{request.Path}' replaced its stable anchor and cannot remint.");
        }

        tSamePath = stepWatch.ElapsedMilliseconds;

        stepWatch.Restart();

        var reservation = await _authority.FindByAnchorAsync(
            anchorToken,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            reservation = await ReserveGenesisAsync(
                request,
                root,
                anchorToken,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateAdoptionCandidate(reservation, request);
        }

        tReservation = stepWatch.ElapsedMilliseconds;

        stepWatch.Restart();

        var capsulePath = await _capsules.PublishGenesisAsync(
            reservation,
            root,
            request.Path,
            cancellationToken).ConfigureAwait(false);
        tCapsule = stepWatch.ElapsedMilliseconds;
        stepWatch.Restart();
        var snapshot = await _capsules.ReadValidatedAsync(
            capsulePath,
            reservation.CapsuleId,
            reservation.ItemKind,
            cancellationToken).ConfigureAwait(false);
        tRead = stepWatch.ElapsedMilliseconds;
        stepWatch.Restart();
        snapshot = await _publisher.ReconcileContentAsync(
            request,
            capsulePath,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        tReconcile = stepWatch.ElapsedMilliseconds;

        stepWatch.Restart();
        var aliases = await _publisher.GetOrPublishAliasesAsync(
            request,
            reservation,
            capsulePath,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        tAliases = stepWatch.ElapsedMilliseconds;

        stepWatch.Restart();

        foreach (var alias in aliases)
        {
            await _bindings.BindAsync(
                alias,
                request.ItemId,
                snapshot.ContentHead.ContentRoot,
                cancellationToken).ConfigureAwait(false);
        }

        await _bindings.UpdateCurrentPathAsync(
            anchorToken,
            request.Path,
            cancellationToken).ConfigureAwait(false);
        tBind = stepWatch.ElapsedMilliseconds;
        _logger.LogInformation(
            "permalink ensure timing item={ItemId} total={Total}ms available={Available} anchor={Anchor} samePath={SamePath} reservation={Reservation} capsule={Capsule} readCapsule={ReadCapsule} reconcile={Reconcile} aliases={Aliases} bind={Bind} aliasCount={AliasCount}",
            request.ItemId,
            totalWatch.ElapsedMilliseconds,
            tAvailable,
            tAnchor,
            tSamePath,
            tReservation,
            tCapsule,
            tRead,
            tReconcile,
            tAliases,
            tBind,
            aliases.Count);

        return new PermalinkStoredState(
            reservation.CapsuleId,
            snapshot.ContentHead.ContentRoot,
            aliases);
    }

    private async Task<PermalinkGenesisReservation> ReserveGenesisAsync(
        PermalinkStoreRequest request,
        string root,
        string anchorToken,
        CancellationToken cancellationToken)
    {
        var issuance = request.PublishAlias && request.PreferredAlias is null
            ? await _transitions.AllocateAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var capsuleId = Guid.NewGuid();
        var capsulePath = _capsules.GetCapsulePath(
            root,
            request.Path,
            request.ItemKind,
            capsuleId);
        var candidate = _documents.CreateGenesis(
            request,
            root,
            anchorToken,
            capsulePath,
            capsuleId,
            issuance);
        return await _authority.ReserveGenesisAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureReachableOrVirtualSeason(PermalinkStoreRequest request)
    {
        if (File.Exists(request.Path) || Directory.Exists(request.Path))
        {
            return;
        }

        if (request.ItemKind == "Season"
            && request.Path.Contains(
                $"{Path.DirectorySeparatorChar}permalink-virtual-seasons{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            _fileSystem.CreateDirectoryDurable(request.Path);
            return;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "content-unreachable",
            $"Permalink content is unreachable at '{request.Path}'.");
    }

    private string ResolveRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var root in _contentRoots)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return root;
            }
        }

        throw new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "content-root-missing",
            $"Path '{path}' is outside every configured permalink content root.");
    }

    private static void ValidateAdoptionCandidate(
        PermalinkGenesisReservation reservation,
        PermalinkStoreRequest request)
    {
        if (!string.Equals(reservation.ItemKind, request.ItemKind, StringComparison.Ordinal))
        {
            throw Conflict(
                "kind-mismatch",
                $"Anchor belongs to {reservation.ItemKind}, not {request.ItemKind}.");
        }

        if (!string.Equals(reservation.CurrentPath, request.Path, StringComparison.Ordinal)
            && (File.Exists(reservation.CurrentPath) || Directory.Exists(reservation.CurrentPath)))
        {
            throw Conflict(
                "copied-anchor",
                $"Stable anchor is live at both '{reservation.CurrentPath}' and '{request.Path}'; copied binding must be forked.");
        }
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
