// SlopTank modification notice: added or changed by SlopTank on 2026-10-02.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by PermalinkAnchorIntentRecoveryTests.cs in the unit test project

/// <summary>
/// Startup crash recovery for anchor-intent registry rows (c170 P6, "Anchor creation under
/// intent"). A row still <c>pending</c> when the server starts never saw Ensure's transaction
/// commit (commit flips it in that same transaction), so its anchor is unsettled by
/// construction: the xattr is removed if it still equals the row's token, and the row is marked
/// abandoned through <see cref="PermalinkAnchorIntentSettlement"/>, the same path a lease
/// dispose takes. Rows are never deleted and never reconciled by age; a row whose root cannot be
/// resolved to a KNOWN identity, or that has no token, stays pending and is logged.
/// </summary>
internal sealed class PermalinkAnchorIntentRecovery : IHostedService
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkPathEvidenceStore _evidence;
    private readonly PermalinkRootIdentityStore _rootStore;
    private readonly PermalinkRootIdentityRegistry _roots;
    private readonly PermalinkAnchorIntentSettlement _settlement;
    private readonly ILogger<PermalinkAnchorIntentRecovery> _logger;

    public PermalinkAnchorIntentRecovery(
        PermalinkAuthorityStore authority,
        PermalinkPathEvidenceStore evidence,
        PermalinkRootIdentityStore rootStore,
        PermalinkRootIdentityRegistry roots,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects,
        ILogger<PermalinkAnchorIntentRecovery> logger)
    {
        _authority = authority;
        _evidence = evidence;
        _rootStore = rootStore;
        _roots = roots;
        _settlement = new PermalinkAnchorIntentSettlement(evidence, fileSystem, objects);
        _logger = logger;
    }

    private enum RowOutcome
    {
        LeftPending,
        Abandoned,
        AbandonedAnchorRemoved
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_authority.IsConfigured)
        {
            return;
        }

        try
        {
            var result = await RecoverAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Permalink anchor-intent recovery: {Examined} pending rows examined, {Abandoned} abandoned "
                + "({Removed} anchors removed), {LeftPending} left pending.",
                result.Examined,
                result.Abandoned,
                result.AnchorsRemoved,
                result.LeftPending);
        }
        catch (PermalinkException exception)
            when (exception.Kind == PermalinkErrorKind.Unavailable)
        {
            // Same stance as the operation reconciler: an unreachable authority must not stop the
            // server from booting; pending intents stay pending and are retried on the next start.
            _logger.LogWarning(
                exception,
                "Permalink anchor-intent recovery skipped: the issuance authority is unavailable "
                + "({Code}). Pending anchor intents stay pending until the next start.",
                exception.Code);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Reconciles every pending anchor-intent row once. A failing row is logged and left pending;
    /// it never stops the pass.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome counts.</returns>
    public async Task<PermalinkAnchorIntentRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        var rows = await _evidence.ListPendingAnchorIntentsAsync(cancellationToken).ConfigureAwait(false);
        var abandoned = 0;
        var removed = 0;
        var leftPending = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await RecoverRowAsync(row, cancellationToken).ConfigureAwait(false))
                {
                    case RowOutcome.AbandonedAnchorRemoved:
                        abandoned++;
                        removed++;
                        break;
                    case RowOutcome.Abandoned:
                        abandoned++;
                        break;
                    default:
                        leftPending++;
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                leftPending++;
                _logger.LogWarning(
                    exception,
                    "Anchor-intent row {RegistryId} ('{Path}' under root {RootIdentityId}) left pending: "
                    + "recovery failed.",
                    row.RegistryId,
                    row.NormalizedRelativePath,
                    row.RootIdentityId);
            }
        }

        return new PermalinkAnchorIntentRecoveryResult(rows.Count, abandoned, removed, leftPending);
    }

    private static bool IsSafeRelativePath(string relative)
    {
        foreach (var component in relative.Split('/'))
        {
            if (component.Length == 0 || component == "." || component == "..")
            {
                return false;
            }
        }

        return true;
    }

    private async Task<RowOutcome> RecoverRowAsync(PermalinkAnchorIntentRow row, CancellationToken cancellationToken)
    {
        if (row.AnchorToken is null)
        {
            _logger.LogWarning(
                "Anchor-intent row {RegistryId} ('{Path}' under root {RootIdentityId}) left pending: it has no "
                + "anchor token (it predates the token column), so its xattr cannot be told from a foreign one.",
                row.RegistryId,
                row.NormalizedRelativePath,
                row.RootIdentityId);
            return RowOutcome.LeftPending;
        }

        if (!IsSafeRelativePath(row.NormalizedRelativePath))
        {
            _logger.LogWarning(
                "Anchor-intent row {RegistryId} left pending: its key '{Path}' is not a valid relative path.",
                row.RegistryId,
                row.NormalizedRelativePath);
            return RowOutcome.LeftPending;
        }

        var rootPath = await ResolveRootAsync(row, cancellationToken).ConfigureAwait(false);
        if (rootPath is null)
        {
            return RowOutcome.LeftPending;
        }

        var path = Path.Combine(rootPath, row.NormalizedRelativePath);
        var anchorRemoved = await _settlement.AbandonAsync(row.RegistryId, path, row.AnchorToken, cancellationToken)
            .ConfigureAwait(false);
        return anchorRemoved ? RowOutcome.AbandonedAnchorRemoved : RowOutcome.Abandoned;
    }

    private async Task<string?> ResolveRootAsync(PermalinkAnchorIntentRow row, CancellationToken cancellationToken)
    {
        var registered = await _rootStore.FindByIdAsync(row.RootIdentityId, cancellationToken).ConfigureAwait(false);
        if (registered is null)
        {
            _logger.LogWarning(
                "Anchor-intent row {RegistryId} left pending: root identity {RootIdentityId} is not registered.",
                row.RegistryId,
                row.RootIdentityId);
            return null;
        }

        try
        {
            var identity = await _roots.EnsureKnownAsync(registered.CanonicalRoot, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(identity.RootIdentityId, row.RootIdentityId, StringComparison.Ordinal))
            {
                return identity.CanonicalRoot;
            }

            _logger.LogWarning(
                "Anchor-intent row {RegistryId} left pending: root '{Root}' now resolves to identity "
                + "{Resolved}, not {RootIdentityId}.",
                row.RegistryId,
                registered.CanonicalRoot,
                identity.RootIdentityId,
                row.RootIdentityId);
            return null;
        }
        catch (PermalinkException exception)
            when (exception.Kind == PermalinkErrorKind.Unavailable)
        {
            _logger.LogWarning(
                exception,
                "Anchor-intent row {RegistryId} ('{Path}' under root {RootIdentityId}) left pending: root "
                + "'{Root}' is unknown or unavailable ({Code}).",
                row.RegistryId,
                row.NormalizedRelativePath,
                row.RootIdentityId,
                registered.CanonicalRoot,
                exception.Code);
            return null;
        }
    }
}
