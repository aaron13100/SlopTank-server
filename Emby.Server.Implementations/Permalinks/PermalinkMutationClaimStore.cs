// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-01.
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Atomically claims and decides every predecessor in one immutable mutation bundle.
/// </summary>
internal sealed class PermalinkMutationClaimStore
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;
    private readonly PermalinkOperationJournal? _journal;

    public PermalinkMutationClaimStore(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkMutationClaimStore"/> class.
    /// </summary>
    /// <remarks>Verifies completed dependency leases through the normal journal reader.</remarks>
    /// <param name="authority">The private authority.</param>
    /// <param name="timeProvider">The admission clock.</param>
    /// <param name="journal">The normal hot/archive operation reader.</param>
    public PermalinkMutationClaimStore(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider,
        PermalinkOperationJournal journal)
        : this(authority, timeProvider)
    {
        _journal = journal;
    }

    /// <summary>
    /// Inserts all claims or rolls the transaction back without leaving a losing subset.
    /// </summary>
    /// <param name="bundle">The immutable mutation bundle.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClaimAsync(
        PermalinkMutationBundle bundle,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var claim in bundle.Claims)
        {
            var owner = await ReadOwnerAsync(
                connection,
                transaction,
                claim,
                cancellationToken).ConfigureAwait(false);
            if (owner is not null
                && !string.Equals(owner, operationId.ToString("D"), StringComparison.Ordinal))
            {
                if (!await AcquireCompletedDependencyAsync(
                        connection, transaction, claim, owner, operationId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw Conflict(
                        "mutation-conflict",
                        $"Capsule '{claim.CapsuleId}' predecessor '{claim.Predecessor}' "
                        + $"is already claimed by '{owner}'.");
                }
            }

            if (owner is null)
            {
                await InsertClaimAsync(
                    connection,
                    transaction,
                    claim,
                    operationId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AcquireCompletedDependencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkMutationClaim claim,
        string owner,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // A path election also records no_change. Only an aggregate dependency,
        // proved by its immutable bundle AND committed journal, is reusable.
        if (claim.Kind != "aggregate" || _journal is null
            || !Guid.TryParseExact(owner, "D", out var previousOperationId)
            || _journal.GetTerminalPhase(previousOperationId) != PermalinkPhase.Committed)
        {
            return false;
        }

        var previous = await _journal.ReadAsync(previousOperationId, cancellationToken)
            .ConfigureAwait(false);
        var committed = await _journal.ReadPhaseAsync(
            previousOperationId, PermalinkPhase.Committed, cancellationToken).ConfigureAwait(false);
        if (previous is null || !previous.OperationId.Equals(previousOperationId)
            || previous.Type != "sloptank.permalink-operation"
            || committed is null || !committed.OperationId.Equals(previousOperationId)
            || committed.State != PermalinkPhase.Committed.Name
            || committed.Type != "sloptank.permalink-operation-phase")
        {
            return false;
        }

        var dependencies = previous?.Bundle?.Claims.Where(value =>
            value.CapsuleId.Equals(claim.CapsuleId)
            && string.Equals(value.Predecessor, claim.Predecessor, StringComparison.Ordinal))
            .ToArray();
        if (dependencies is not [{ Kind: "aggregate" }])
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE MutationClaims
               SET operation_id = $operation, created_at = $created
             WHERE capsule_id = $capsule AND predecessor_root = $predecessor
               AND operation_id = $owner
               AND EXISTS (
                   SELECT 1 FROM OperationDecisions
                    WHERE operation_id = $owner AND capsule_id = $capsule
                      AND predecessor = $predecessor AND decision = 'no_change')
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$created", UtcNow());
        command.Parameters.AddWithValue("$capsule", claim.CapsuleId.ToString("D"));
        command.Parameters.AddWithValue("$predecessor", claim.Predecessor);
        command.Parameters.AddWithValue("$owner", owner);
        // Historical decisions and journals are immutable; only the live
        // dependency slot changes owner in this all-or-nothing admission.
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Releases only this operation's unelected reservations for a proved no-effect result.</summary>
    /// <param name="operation">The exact Ready operation whose live bytes were revalidated.</param>
    /// <param name="cancellationToken">The durable operation lifetime.</param>
    /// <returns>A task representing atomic no-effect reservation settlement.</returns>
    public async Task SettleNoEffectAsync(
        PermalinkOperationDocument operation,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var claim in PermalinkMediaMutation.RequireBundle(operation).Claims)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT 1 FROM ContentTransitionClaims
                 WHERE capsule_id = $capsule AND operation_id = $operation
                UNION ALL
                SELECT 1 FROM PathOperationDecisions
                 WHERE capsule_id = $capsule AND operation_id = $operation
                UNION ALL
                SELECT 1 FROM OperationDecisions
                 WHERE capsule_id = $capsule AND operation_id = $operation
                   AND predecessor = $predecessor AND decision <> 'no_change'
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("$capsule", claim.CapsuleId.ToString("D"));
            command.Parameters.AddWithValue("$operation", operation.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$predecessor", claim.Predecessor);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw Conflict("no-effect-transition-elected", "A no-effect result cannot release an elected content or path transition.");
            }

            var owner = await ReadOwnerAsync(connection, transaction, claim, cancellationToken).ConfigureAwait(false);
            if (owner != operation.OperationId.ToString("D"))
            {
                // A competing owner is untouched: a no-effect operation never
                // acquires, supersedes or releases that owner's election.
                continue;
            }

            if (_journal is null || _journal.HasPhase(operation.OperationId, PermalinkPhase.Published))
            {
                throw Conflict("no-effect-proof-unavailable", "An owned reservation requires exact journal proof of nonpublication.");
            }

            var ready = await _journal.ReadPhaseAsync(
                operation.OperationId, PermalinkPhase.Ready, cancellationToken).ConfigureAwait(false);
            if (ready is null || !ready.OperationId.Equals(operation.OperationId) || ready.ContentRoot != operation.OldContentRoot)
            {
                throw Conflict("no-effect-proof-unavailable", "The exact Ready record does not prove unchanged desired content.");
            }

            await InsertDecisionAsync(
                connection,
                transaction,
                claim,
                operation.OperationId,
                "no_change",
                cancellationToken).ConfigureAwait(false);
            command.CommandText = """
                DELETE FROM MutationClaims WHERE capsule_id = $capsule
                   AND predecessor_root = $predecessor AND operation_id = $operation
                """;
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("claim-owner-mismatch", "A no-effect reservation changed owner during guarded settlement.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the exact operation owns every claim in its immutable bundle.
    /// </summary>
    /// <param name="bundle">The immutable mutation bundle.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<bool> OwnsAsync(
        PermalinkMutationBundle bundle,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var claim in bundle.Claims)
        {
            if (!string.Equals(
                    await ReadOwnerAsync(connection, null, claim, cancellationToken)
                        .ConfigureAwait(false),
                    operationId.ToString("D"),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Chooses one immutable terminal outcome per claimed predecessor in one transaction.
    /// </summary>
    /// <param name="bundle">The immutable mutation bundle.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="sourceDecision">The source decision.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task FinalizeAsync(
        PermalinkMutationBundle bundle,
        Guid operationId,
        string sourceDecision,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var claim in bundle.Claims)
        {
            var owner = await ReadOwnerAsync(
                connection,
                transaction,
                claim,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(owner, operationId.ToString("D"), StringComparison.Ordinal))
            {
                throw Conflict(
                    "claim-owner-mismatch",
                    $"Operation '{operationId}' does not own capsule '{claim.CapsuleId}'.");
            }

            var decision = claim.Kind == "content" ? sourceDecision : "no_change";
            await InsertDecisionAsync(
                connection,
                transaction,
                claim,
                operationId,
                decision,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkMutationClaim claim,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MutationClaims
                (capsule_id, predecessor_root, operation_id, created_at)
            VALUES ($capsule, $predecessor, $operation, $created)
            """;
        command.Parameters.AddWithValue("$capsule", claim.CapsuleId.ToString("D"));
        command.Parameters.AddWithValue("$predecessor", claim.Predecessor);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$created", UtcNow());
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "mutation-conflict",
                $"Mutation claim for capsule '{claim.CapsuleId}' lost a concurrent election.",
                exception);
        }
    }

    private async Task InsertDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PermalinkMutationClaim claim,
        Guid operationId,
        string decision,
        CancellationToken cancellationToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{operationId:D}\0{claim.CapsuleId:D}\0{claim.Predecessor}\0{decision}"));
        var eventId = new Guid(digest.AsSpan(0, 16)).ToString("D");
        var digestText = "sha256:" + Convert.ToHexStringLower(digest);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO OperationDecisions
                (operation_id, capsule_id, predecessor, decision,
                 event_id, event_digest, created_at)
            VALUES
                ($operation, $capsule, $predecessor, $decision,
                 $event, $digest, $created)
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$capsule", claim.CapsuleId.ToString("D"));
        command.Parameters.AddWithValue("$predecessor", claim.Predecessor);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$digest", digestText);
        command.Parameters.AddWithValue("$created", UtcNow());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            SELECT decision
              FROM OperationDecisions
             WHERE operation_id = $operation
               AND capsule_id = $capsule
               AND predecessor = $predecessor
            """;
        var stored = (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(stored, decision, StringComparison.Ordinal))
        {
            throw Conflict(
                "operation-decision-mismatch",
                $"Operation '{operationId}' already has a different terminal decision.");
        }
    }

    private static async Task<string?> ReadOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PermalinkMutationClaim claim,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id
              FROM MutationClaims
             WHERE capsule_id = $capsule
               AND predecessor_root = $predecessor
            """;
        command.Parameters.AddWithValue("$capsule", claim.CapsuleId.ToString("D"));
        command.Parameters.AddWithValue("$predecessor", claim.Predecessor);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
