// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by PermalinkAuthoritySnapshotPublicationTests.cs in the unit test project

/// <summary>
/// The production-owned lifecycle that keeps <c>CURRENT</c> published for the configured
/// authority. <see cref="PermalinkAuthoritySnapshotPublisher.PublishAsync"/> is the only
/// writer of snapshot generations, and this service is its only production caller: a
/// published generation must exist before any cross-process writer can be admitted, and it
/// must advance whenever the authority counters do, or every subsequent admission names a
/// stale generation and is refused.
///
/// Freshness passes are idempotent and bounded by write activity, never by reads: when
/// <c>CURRENT</c> already publishes the live authority id, schema epoch, content epoch and
/// journal counter, nothing is allocated. Passes are triggered three ways: once at startup
/// (before the service finishes starting, so a restarted server publishes before serving),
/// after every completed authorized authority write (the store raises
/// <see cref="PermalinkAuthorityStore.AuthorizedWriteCompleted"/>), and by a periodic
/// maintenance tick that covers writers the process never sees (out-of-process admitted
/// writers). A failed publication is logged and leaves the prior valid <c>CURRENT</c> in
/// place; readers keep failing closed on the typed snapshot validation until a later pass
/// succeeds. An unconfigured authority disables the lifecycle entirely rather than
/// manufacturing any authority or snapshot state.
/// </summary>
internal sealed class PermalinkAuthoritySnapshotPublicationService : IHostedService, IDisposable
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaintenanceDueTime = TimeSpan.FromSeconds(30);

    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkAuthoritySnapshotPublisher _publisher;
    private readonly ILogger<PermalinkAuthoritySnapshotPublicationService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Timer? _maintenanceTimer;
    private CancellationTokenSource? _stopping;
    private int _refreshQueued;

    /// <summary>Initializes a new instance of the <see cref="PermalinkAuthoritySnapshotPublicationService"/> class.</summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="publisher">The snapshot publisher whose lifecycle this service owns.</param>
    /// <param name="logger">The logger.</param>
    public PermalinkAuthoritySnapshotPublicationService(
        PermalinkAuthorityStore authority,
        PermalinkAuthoritySnapshotPublisher publisher,
        ILogger<PermalinkAuthoritySnapshotPublicationService> logger)
    {
        _authority = authority;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Raised (test seam) whenever one refresh pass completes, successfully or not.
    /// </summary>
    internal event Action? RefreshCompleted;

    /// <summary>
    /// Test seam: runs one background refresh pass exactly as the maintenance tick and the
    /// authorized-write signal schedule it, so a test can await the pass that must not let
    /// an unexpected failure escape as an unobserved task exception.
    /// </summary>
    /// <returns>A task representing the background pass.</returns>
    internal Task RunRefreshPassForTestAsync()
    {
        Interlocked.Increment(ref _refreshQueued);
        return RunRefreshInBackgroundAsync();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_authority.IsConfigured)
        {
            _logger.LogInformation(
                "Permalink authority is not configured; authority snapshot publication stays disabled.");
            return;
        }

        _stopping = new CancellationTokenSource();
        _authority.AuthorizedWriteCompleted += OnAuthorizedWriteCompleted;
        try
        {
            await RunOnePassAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown during the startup publication; the maintenance tick and the
            // next start own it from here. The prior CURRENT, if any, stays published.
        }
        catch (PermalinkException exception)
        {
            // Same stance as the recovery reconcilers: an authority that cannot be read or
            // published must not stop the server from booting. No CURRENT exists yet (or
            // the prior one stays), and every reader keeps failing closed.
            _logger.LogWarning(
                exception,
                "Startup permalink authority snapshot publication failed ({Kind} {ErrorCode}); readers stay fail-closed until a later pass succeeds.",
                exception.Kind,
                exception.Code);
        }

        _maintenanceTimer = new Timer(
            _ => QueueRefresh(), null, MaintenanceDueTime, MaintenanceInterval);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null)
        {
            return;
        }

        _authority.AuthorizedWriteCompleted -= OnAuthorizedWriteCompleted;
        if (_maintenanceTimer is not null)
        {
            await _maintenanceTimer.DisposeAsync().ConfigureAwait(false);
            _maintenanceTimer = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _stopping = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _maintenanceTimer?.Dispose();
        _stopping?.Dispose();
        _refreshLock.Dispose();
    }

    /// <summary>
    /// Runs one freshness pass now. Publishes a new generation when <c>CURRENT</c> does not
    /// publish the live authority counters; allocates nothing when it already does.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="PermalinkException">The publication was attempted and failed.</exception>
    public async Task EnsurePublishedAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (await _publisher.IsCurrentFreshAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var manifest = await _publisher.PublishAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Published permalink authority snapshot generation {Generation} (content epoch {ContentEpoch}, journal counter {JournalCounter}).",
            manifest.Generation,
            manifest.ContentEpoch,
            manifest.JournalCounter);
    }

    private void OnAuthorizedWriteCompleted()
    {
        // Raised on arbitrary thread contexts, possibly inside the writer's transaction
        // scope: never block here, only schedule a coalesced background pass. Signals that
        // arrive while a pass is already draining are counted, not dropped: the drain loop
        // replays one pass per signal so a write that commits mid-pass still publishes.
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        Interlocked.Increment(ref _refreshQueued);
        _ = Task.Run(RunRefreshInBackgroundAsync);
    }

    private async Task RunOnePassAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsurePublishedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RunRefreshInBackgroundAsync()
    {
        try
        {
            await _refreshLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                while (Interlocked.Exchange(ref _refreshQueued, 0) > 0)
                {
                    await EnsurePublishedAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        catch (PermalinkException exception)
        {
            _logger.LogWarning(
                exception,
                "Permalink authority snapshot publication failed ({Kind} {ErrorCode}); the previously published CURRENT remains in force and readers stay fail-closed until a later pass succeeds.",
                exception.Kind,
                exception.Code);
        }
        catch (Exception exception)
        {
            // This task is deliberately detached from the triggering write/timer. Preserve
            // an unexpected failure here, where it still has its stack and publication
            // context, rather than letting it become an unobserved task exception. The next
            // queued write or maintenance tick owns the retry; CURRENT is never rewritten by
            // this failed pass.
            _logger.LogError(
                exception,
                "Unexpected permalink authority snapshot publication failure; the previously published CURRENT remains in force and readers stay fail-closed until a later pass succeeds.");
        }
        finally
        {
            RefreshCompleted?.Invoke();
        }
    }
}
