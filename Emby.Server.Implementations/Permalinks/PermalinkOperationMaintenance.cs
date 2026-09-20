// SlopTank modification notice: added or changed by SlopTank on 2026-09-20.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Provides one-shot maintenance access to the production permalink scratch reclaim code.
/// </summary>
public static class PermalinkOperationMaintenance
{
    /// <summary>
    /// Reclaims scratch for explicitly selected terminal operations without starting Jellyfin.
    /// </summary>
    /// <param name="authorityRoot">The configured permalink authority root.</param>
    /// <param name="operationIds">The exact operation ids selected by the operator.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The selected maintenance result.</returns>
    public static async Task<PermalinkOperationScratchMaintenanceResult> ReclaimTerminalScratchAsync(
        string authorityRoot,
        IReadOnlyCollection<Guid> operationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityRoot);
        ArgumentNullException.ThrowIfNull(operationIds);
        if (operationIds.Count == 0)
        {
            throw new ArgumentException("At least one operation id is required.", nameof(operationIds));
        }

        if (operationIds.Count > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationIds),
                "One-shot permalink maintenance is bounded to 10000 operation ids.");
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Permalinks:AuthorityRoot"] = authorityRoot,
            })
            .Build();
        var mountPolicy = new MacPermalinkMountPolicy(configuration);
        var fileSystem = new MacPermalinkAtomicFileSystem(mountPolicy);
        var timeProvider = TimeProvider.System;
        using var authority = new PermalinkAuthorityStore(configuration, fileSystem, timeProvider);
        using var journal = new PermalinkOperationJournal(
            authority,
            fileSystem,
            configuration,
            timeProvider,
            NullLogger<PermalinkOperationJournal>.Instance);
        var result = await journal.ReclaimTerminalOperationScratchAsync(
                operationIds,
                cancellationToken)
            .ConfigureAwait(false);
        return new PermalinkOperationScratchMaintenanceResult(
            result.Examined,
            result.Reclaimed,
            result.Retained,
            result.Failed,
            result.MaximumOperationsPerPass);
    }
}
