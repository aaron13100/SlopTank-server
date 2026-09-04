using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private sloptank-tests PermalinkOperationReconcilerTests.cs

/// <summary>
/// Recovers abandoned permalink operations when the server starts.
/// </summary>
internal sealed class PermalinkOperationReconciler : IHostedService
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly IPermalinkMutationCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PermalinkOperationReconciler> _logger;
    private readonly TimeSpan _abandonmentGracePeriod;

    public PermalinkOperationReconciler(
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        IPermalinkMutationCoordinator coordinator,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<PermalinkOperationReconciler> logger)
    {
        _authority = authority;
        _journal = journal;
        _coordinator = coordinator;
        _timeProvider = timeProvider;
        _logger = logger;
        _abandonmentGracePeriod = configuration.GetValue(
            "Permalinks:MutationAbandonmentGracePeriod",
            TimeSpan.FromMinutes(15));
        if (_abandonmentGracePeriod < TimeSpan.Zero)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "mutation-abandonment-grace-invalid",
                "Permalinks:MutationAbandonmentGracePeriod cannot be negative.");
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_authority.IsConfigured)
        {
            return;
        }

        // Priming the journal's pending index IS this walk. Both this method and
        // the index used to enumerate the same directories and compute the same
        // settled/pending split, so the server paid for it twice: once here on
        // the boot path, and again on whichever request first asked whether an
        // item was fenced. Measured on production 2026-09-02 with 61,702
        // operation directories, that second walk cost the first watch link
        // after every restart 124.66s, against 0.97s once it was built.
        //
        // Reconciliation wants exactly the operations this returns, because the
        // pending set and the set this loop used to keep are the same set: both
        // are the directories whose GetSettledPhase is null.
        var startedAt = Stopwatch.GetTimestamp();
        var priming = await _journal.PrimePendingIndexAsync(cancellationToken).ConfigureAwait(false);
        var primedAt = Stopwatch.GetElapsedTime(startedAt);

        var now = _timeProvider.GetUtcNow();
        foreach (var operationId in priming.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileAsync(operationId, now, cancellationToken).ConfigureAwait(false);
        }

        // The generic host awaits this method before the server accepts a
        // request, so whatever it costs is added to every restart, and the walk
        // it contains is the only part that grows with the journal. Log the two
        // apart from each other: reconciling N abandoned operations and
        // enumerating M directories to find them are different costs with
        // different fixes.
        _logger.LogInformation(
            "Permalink reconciler start: {ElapsedMs:F1} ms total, {PrimeMs:F1} ms enumerating "
            + "{Directories} operation directories, {Pending} pending operations reconciled.",
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            primedAt.TotalMilliseconds,
            priming.Walk?.Directories ?? -1,
            priming.Pending.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task ReconcileAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await _journal.ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is null
                || !DateTimeOffset.TryParse(
                    operation.CreatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt))
            {
                _logger.LogError(
                    "Permalink operation {OperationId} has no parseable creation timestamp and remains fenced.",
                    operationId);
                return;
            }

            if (now - createdAt < _abandonmentGracePeriod)
            {
                return;
            }

            if (_journal.GetSettledPhase(operationId) is not null)
            {
                return;
            }

            var result = await _coordinator.RecoverAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Recovered permalink operation {OperationId} as {State} after {Age}.",
                operationId,
                result.State,
                now - createdAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to reconcile permalink operation {OperationId}; it remains fenced.",
                operationId);
        }
    }
}
