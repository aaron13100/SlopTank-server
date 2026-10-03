// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-02, 2026-09-03, 2026-09-08, 2026-09-09, 2026-10-01, 2026-10-03.
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
    private readonly PermalinkPathLeaseFactory _leases;
    private readonly ILogger<PermalinkStore> _logger;
    private readonly PermalinkContentRoots _contentRoots;

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
    /// <param name="leases">The path lease factory.</param>
    /// <param name="logger">The logger, used for the per-step ensure timing.</param>
    /// <param name="contentRoots">The configured content roots.</param>
    public PermalinkStore(
        PermalinkAuthorityStore authority,
        PermalinkTransitionStore transitions,
        PermalinkBindingIndex bindings,
        PermalinkCapsuleStore capsules,
        PermalinkDocumentFactory documents,
        PermalinkTransitionPublisher publisher,
        IPermalinkAtomicFileSystem fileSystem,
        PermalinkPathLeaseFactory leases,
        ILogger<PermalinkStore> logger,
        PermalinkContentRoots contentRoots)
    {
        _authority = authority;
        _transitions = transitions;
        _bindings = bindings;
        _capsules = capsules;
        _documents = documents;
        _publisher = publisher;
        _fileSystem = fileSystem;
        _leases = leases;
        _logger = logger;
        _contentRoots = contentRoots;
    }

    /// <inheritdoc />
    public async Task<IPermalinkPathLease> BeginContentIdentityAsync(
        string path,
        string itemKind,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        EnsureReachableOrVirtualSeason(path, itemKind);
        var root = ResolveRoot(path);
        return await _leases.BeginAsync(root, path, IsVirtualSeason(path, itemKind), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        IPermalinkPathLease lease,
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
        EnsureReachableOrVirtualSeason(request.Path, request.ItemKind);
        var root = ResolveRoot(request.Path);
        var pathLease = lease as PermalinkPathLease
            ?? throw Conflict("lease-invalid", "Ensure requires a lease from BeginContentIdentityAsync.");
        if (!string.Equals(
                pathLease.NormalizedRelativePath,
                PermalinkPathKey.Normalize(root, request.Path),
                StringComparison.Ordinal))
        {
            throw Conflict(
                "lease-path-mismatch",
                $"The lease covers '{pathLease.NormalizedRelativePath}', not '{request.Path}'.");
        }

        var anchorToken = pathLease.AnchorToken;
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
                pathLease,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateAdoptionCandidate(reservation, request);
            await _authority.SettleAsync(pathLease.RecheckAndCommitAsync, cancellationToken)
                .ConfigureAwait(false);
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
                reservation.CapsuleId,
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
        PermalinkPathLease lease,
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
            lease.RecheckAndCommitAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureReachableOrVirtualSeason(string path, string itemKind)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return;
        }

        if (IsVirtualSeason(path, itemKind))
        {
            _fileSystem.CreateDirectoryDurable(path);
            return;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "content-unreachable",
            $"Permalink content is unreachable at '{path}'.");
    }

    private static bool IsVirtualSeason(string path, string itemKind)
        => itemKind == "Season"
            && path.Contains(
                $"{Path.DirectorySeparatorChar}permalink-virtual-seasons{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);

    private string ResolveRoot(string path) => _contentRoots.Resolve(path);

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
