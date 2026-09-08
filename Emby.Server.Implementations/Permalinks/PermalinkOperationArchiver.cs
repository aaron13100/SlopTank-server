using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Runs terminal-operation retention outside the request and startup-critical paths.
/// </summary>
internal sealed class PermalinkOperationArchiver : BackgroundService
{
    /// <summary>Gets the deployment gate for automatic journal maintenance.</summary>
    public const string EnabledKey = "Permalinks:OperationJournalArchiveEnabled";

    /// <summary>Gets the delay before the first bounded maintenance pass after startup.</summary>
    public const string InitialDelayKey = "Permalinks:OperationJournalArchiveInitialDelay";

    /// <summary>Gets the delay between bounded maintenance passes.</summary>
    public const string IntervalKey = "Permalinks:OperationJournalArchiveInterval";

    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PermalinkOperationArchiver> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;

    public PermalinkOperationArchiver(
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<PermalinkOperationArchiver> logger)
    {
        _authority = authority;
        _journal = journal;
        _timeProvider = timeProvider;
        _logger = logger;
        _enabled = configuration.GetValue(EnabledKey, false);
        _initialDelay = configuration.GetValue(InitialDelayKey, TimeSpan.FromHours(1));
        // At the default hard cap of 100 operations, a five-minute cadence has a theoretical
        // ceiling of 28,800 terminal moves/day, well above the measured 3,250/day arrival rate.
        // Deployment still requires measuring one bounded pass on the external volume.
        _interval = configuration.GetValue(IntervalKey, TimeSpan.FromMinutes(5));
        if (_initialDelay < TimeSpan.Zero)
        {
            throw InvalidSchedule(InitialDelayKey, "cannot be negative");
        }

        if (_interval <= TimeSpan.Zero)
        {
            throw InvalidSchedule(IntervalKey, "must be positive");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || !_authority.IsConfigured)
        {
            return;
        }

        await Task.Delay(_initialDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _journal.ArchiveTerminalOperationsAsync(stoppingToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Permalink journal archive: {Examined} indexed candidates examined, {Archived} "
                    + "archived, {Retained} within retention, {Refused} non-terminal or invalid, "
                    + "{Failed} safely retained after failure.",
                    result.Examined,
                    result.Archived,
                    result.Retained,
                    result.Refused,
                    result.Failed);
                if (result.Examined == result.MaximumOperationsPerPass)
                {
                    _logger.LogInformation(
                        "Permalink journal archive reached its {Maximum}-operation pass ceiling; "
                        + "any remaining hot entries are deferred to the next bounded pass.",
                        result.MaximumOperationsPerPass);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Permalink operation archival failed closed; hot journal records were retained.");
            }

            await Task.Delay(_interval, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private static PermalinkException InvalidSchedule(string key, string constraint)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "operation-archive-schedule-invalid",
            $"{key} {constraint}.");
    }
}
