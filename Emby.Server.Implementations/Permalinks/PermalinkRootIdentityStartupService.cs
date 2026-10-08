// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by PermalinkRootIdentityStartupServiceTests.cs in the unit test project

/// <summary>
/// Establishes the stable identity of every configured root at startup, before the
/// server becomes ready and before the first authority snapshot is published. The
/// census (<see cref="IPermalinkRootIdentityCensus"/>) registers brand-new content
/// and evidence roots and re-attests known ones; without this pass an evidence
/// root's identity row exists only after some other path happens to touch it
/// (scans ensure content roots through path leases, but nothing ever scans an
/// evidence root), so a worker handed that root as a writer-admission deployment
/// input is refused client-side before the server is ever asked.
/// Registered before <see cref="PermalinkAuthoritySnapshotPublicationService"/>
/// so the first published usable snapshot already contains the initialized
/// identities: a registration advances content_epoch, which makes any pre-existing
/// CURRENT stale and forces the publication pass that follows to publish the rows
/// this pass wrote. The census is resolved inside the pass so an invalid roots
/// configuration (for example an evidence root overlapping a content root) keeps
/// its typed refusal at the consumer instead of aborting the boot, matching the
/// recovery reconcilers' stance: the failure is logged, the boot continues, and
/// every admission that needs a missing identity keeps failing closed. An
/// unconfigured authority manufactures nothing.
/// </summary>
internal sealed class PermalinkRootIdentityStartupService : IHostedService
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly IServiceProvider _provider;
    private readonly ILogger<PermalinkRootIdentityStartupService> _logger;

    /// <summary>Initializes a new instance of the <see cref="PermalinkRootIdentityStartupService"/> class.</summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="provider">The service provider the census is resolved from.</param>
    /// <param name="logger">The logger.</param>
    public PermalinkRootIdentityStartupService(
        PermalinkAuthorityStore authority,
        IServiceProvider provider,
        ILogger<PermalinkRootIdentityStartupService> logger)
    {
        _authority = authority;
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_authority.IsConfigured)
        {
            _logger.LogInformation(
                "Permalink authority is not configured; configured-root identity initialization stays disabled.");
            return;
        }

        try
        {
            await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            var census = _provider.GetRequiredService<IPermalinkRootIdentityCensus>();
            var roots = await census.ReadAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Permalink root identity census ensured {Count} configured and historical roots at startup.",
                roots.RootGenerations.Count);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown during the startup census: the roots already ensured stay
            // registered, and the next start re-attests and completes the rest.
        }
        catch (PermalinkException exception)
        {
            // A configured root that cannot be validated (or a conflicting roots
            // configuration, which refuses while the census is being resolved) must
            // not stop the server from booting. No identity is invented for it; every
            // admission that needs the missing identity keeps failing closed until
            // the configuration is repaired and the server restarts into a census
            // that can attest it.
            _logger.LogWarning(
                exception,
                "Startup permalink root identity initialization failed ({Kind} {ErrorCode}); admissions that need these identities stay refused until a later start succeeds.",
                exception.Kind,
                exception.Code);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
