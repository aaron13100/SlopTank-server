// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Globalization;
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

    public PermalinkMutationClaimStore(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
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
                throw Conflict(
                    "mutation-conflict",
                    $"Capsule '{claim.CapsuleId}' predecessor '{claim.Predecessor}' "
                    + $"is already claimed by '{owner}'.");
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
