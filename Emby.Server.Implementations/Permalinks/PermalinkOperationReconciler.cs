using System;
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
/// Terminates abandoned unpublished permalink operations when the server starts.
/// </summary>
internal sealed class PermalinkOperationReconciler : IHostedService
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkOperationJournal _journal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PermalinkOperationReconciler> _logger;
    private readonly TimeSpan _abandonmentGracePeriod;

    public PermalinkOperationReconciler(
        PermalinkAuthorityStore authority,
        PermalinkOperationJournal journal,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<PermalinkOperationReconciler> logger)
    {
        _authority = authority;
        _journal = journal;
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

        var root = Path.Combine(_authority.Root, ".sloptank", "permalinks", "operations");
        if (!Directory.Exists(root))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(directory), out var operationId)
                || _journal.GetTerminalPhase(operationId) is not null)
            {
                continue;
            }

            await ReconcileAsync(operationId, now, cancellationToken).ConfigureAwait(false);
        }
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
            // Published work may have changed authoritative bytes and therefore remains fenced
            // until the existing recovery or administrator-resolution workflow closes it.
            if (_journal.HasPhase(operationId, "published"))
            {
                _logger.LogWarning(
                    "Published permalink operation {OperationId} remains fenced for explicit recovery.",
                    operationId);
                return;
            }

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

            if (_journal.GetTerminalPhase(operationId) is not null
                || _journal.HasPhase(operationId, "published"))
            {
                return;
            }

            await _journal.WritePhaseAsync(
                operationId,
                "aborted",
                new PermalinkOperationPhase(
                    operationId,
                    "aborted",
                    operation.OldContentRoot,
                    now.ToString("O", CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Aborted unpublished permalink operation {OperationId} after {Age}.",
                operationId,
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
