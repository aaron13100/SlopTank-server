// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Persists and atomically consumes exact P9 administrator confirmation challenges.</summary>
internal sealed class PermalinkConfirmationChallengeStore : IPermalinkConfirmationChallengeStore
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] AllowedActions =
    [
        "orphan-closure",
        "exact-restoration",
        "restore-with-rewrite",
        "successor-mint",
        "detached-capsule-recovery",
        "scratch-adoption"
    ];

    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;
    private readonly IPermalinkConfirmationChallengeEntropy _entropy;

    /// <summary>Initializes a production challenge store.</summary>
    /// <param name="authority">The protected permalink authority.</param>
    /// <param name="timeProvider">The server clock.</param>
    public PermalinkConfirmationChallengeStore(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider)
        : this(authority, timeProvider, new CryptographicPermalinkConfirmationChallengeEntropy())
    {
    }

    /// <summary>Initializes a challenge store with an explicit entropy boundary.</summary>
    /// <param name="authority">The protected permalink authority.</param>
    /// <param name="timeProvider">The server clock.</param>
    /// <param name="entropy">The cryptographic entropy source.</param>
    internal PermalinkConfirmationChallengeStore(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider,
        IPermalinkConfirmationChallengeEntropy entropy)
    {
        _authority = authority;
        _timeProvider = timeProvider;
        _entropy = entropy;
    }

    /// <inheritdoc />
    public Task<PermalinkConfirmationChallenge> IssueAsync(
        PermalinkConfirmationChallengeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return GuardStoreAsync(() => IssueCoreAsync(request, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<PermalinkConfirmationChallengeConsumption> ConsumeAsync(
        PermalinkConfirmationChallengePresentation presentation,
        CancellationToken cancellationToken)
        => GuardStoreAsync(() => ConsumeCoreAsync(presentation, cancellationToken), cancellationToken);

    private async Task<PermalinkConfirmationChallenge> IssueCoreAsync(
        PermalinkConfirmationChallengeRequest request,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = PermalinkSqliteTransactions.BeginImmediateSerializable(connection);
        var existing = await ReadByOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var sameTuple = string.Equals(existing.TargetDigest, request.TargetDigest, StringComparison.Ordinal)
                && string.Equals(existing.InventoryGeneration, request.InventoryGeneration, StringComparison.Ordinal)
                && string.Equals(existing.RequestedAction, request.RequestedAction, StringComparison.Ordinal);
            throw Conflict(
                sameTuple ? "confirmation-challenge-already-issued" : "confirmation-challenge-operation-conflict",
                sameTuple
                    ? "This durable operation already has a confirmation challenge."
                    : "This durable operation already has a challenge for a different target tuple.");
        }

        var challengeId = _entropy.NewId();
        var secret = Base64Url(_entropy.GetBytes(32));
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + ChallengeLifetime;
        var challengeDigest = Digest(secret);
        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            request.OperationId,
            "PermalinkConfirmationChallenges",
            "INSERT",
            challengeId.ToString("D"),
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PermalinkConfirmationChallenges
                    (challenge_id, operation_id, challenge_digest, target_digest,
                     inventory_generation, requested_action, expires_at, consumed_at,
                     created_at, updated_at, version, kind, deleted_at)
                VALUES ($challenge, $operation, $secret, $target, $generation, $action,
                        $expires, NULL, $now, $now, 1, 'confirmation', NULL)
                """;
            insert.Parameters.AddWithValue("$challenge", challengeId.ToString("D"));
            insert.Parameters.AddWithValue("$operation", request.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$secret", challengeDigest);
            insert.Parameters.AddWithValue("$target", request.TargetDigest);
            insert.Parameters.AddWithValue("$generation", request.InventoryGeneration);
            insert.Parameters.AddWithValue("$action", request.RequestedAction);
            insert.Parameters.AddWithValue("$expires", Format(expiresAt));
            insert.Parameters.AddWithValue("$now", Format(now));
            try
            {
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Corrupt,
                    "confirmation-challenge-identity-collision",
                    "The authority refused a duplicate confirmation challenge identity.",
                    exception,
                    request.OperationId);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermalinkConfirmationChallenge(
            challengeId,
            request.OperationId,
            secret,
            request.TargetDigest,
            request.InventoryGeneration,
            request.RequestedAction,
            expiresAt);
    }

    private async Task<PermalinkConfirmationChallengeConsumption> ConsumeCoreAsync(
        PermalinkConfirmationChallengePresentation presentation,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = PermalinkSqliteTransactions.BeginImmediateSerializable(connection);
        var row = await ReadByChallengeAsync(connection, transaction, presentation.ChallengeId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict("confirmation-challenge-missing", "The confirmation challenge does not exist.");

        if (row.ConsumedAt is not null)
        {
            throw Conflict("confirmation-challenge-consumed", "The confirmation challenge has already been consumed.");
        }

        if (_timeProvider.GetUtcNow() >= row.ExpiresAt)
        {
            throw Conflict("confirmation-challenge-expired", "The confirmation challenge has expired.");
        }

        RequireEqual(row.OperationId, presentation.OperationId, "operation");
        RequireEqual(row.TargetDigest, presentation.TargetDigest, "target");
        RequireEqual(row.InventoryGeneration, presentation.InventoryGeneration, "generation");
        RequireEqual(row.RequestedAction, presentation.RequestedAction, "action");
        RequireEqual(row.ExpiresAt, presentation.ExpiresAt, "expiry");
        if (!FixedTimeDigestEquals(row.ChallengeDigest, Digest(presentation.Challenge)))
        {
            throw Conflict("confirmation-challenge-secret-mismatch", "The server-issued confirmation secret does not match.");
        }

        using (var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            row.OperationId,
            "PermalinkConfirmationChallenges",
            "UPDATE",
            row.ChallengeId.ToString("D"),
            cancellationToken,
            transaction).ConfigureAwait(false))
        {
            await using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE PermalinkConfirmationChallenges
                   SET consumed_at = $now, updated_at = $now
                 WHERE challenge_id = $challenge AND consumed_at IS NULL
                """;
            consume.Parameters.AddWithValue("$now", Format(_timeProvider.GetUtcNow()));
            consume.Parameters.AddWithValue("$challenge", row.ChallengeId.ToString("D"));
            if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("confirmation-challenge-consumed", "The confirmation challenge was consumed concurrently.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermalinkConfirmationChallengeConsumption(row.ChallengeId, row.OperationId);
    }

    private static void ValidateRequest(PermalinkConfirmationChallengeRequest request)
    {
        if (request.OperationId == Guid.Empty)
        {
            throw Conflict("confirmation-challenge-operation-invalid", "A durable operation identifier is required.");
        }

        if (!IsCanonicalSha256(request.TargetDigest))
        {
            throw Conflict("confirmation-challenge-target-invalid", "The target digest must be canonical lowercase SHA-256.");
        }

        if (!IsCanonicalSha256(request.InventoryGeneration))
        {
            throw Conflict("confirmation-challenge-generation-invalid", "The inventory generation must be a sealed canonical lowercase SHA-256 digest.");
        }

        if (Array.IndexOf(AllowedActions, request.RequestedAction) < 0)
        {
            throw Conflict("confirmation-challenge-action-invalid", "The requested action is not challenge-authorizable.");
        }
    }

    private static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireEqual<T>(T expected, T actual, string field)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw Conflict(
                $"confirmation-challenge-{field}-mismatch",
                $"The presented confirmation {field} does not match the server-issued challenge.");
        }
    }

    private static bool FixedTimeDigestEquals(string expected, string actual)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));

    private static string Digest(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value, Guid operationId)
    {
        if (DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            return parsed;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Corrupt,
            "confirmation-challenge-date-corrupt",
            "The durable confirmation challenge contains an invalid timestamp.",
            operationId: operationId);
    }

    private static async Task<ChallengeRow?> ReadByOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
        => await ReadAsync(
            connection,
            transaction,
            "operation_id = $value",
            operationId.ToString("D"),
            cancellationToken).ConfigureAwait(false);

    private static async Task<ChallengeRow?> ReadByChallengeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid challengeId,
        CancellationToken cancellationToken)
        => await ReadAsync(
            connection,
            transaction,
            "challenge_id = $value",
            challengeId.ToString("D"),
            cancellationToken).ConfigureAwait(false);

    private static async Task<ChallengeRow?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string predicate,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The predicate is selected exclusively by the two private callers above.
        command.CommandText = $"""
            SELECT challenge_id, operation_id, challenge_digest, target_digest,
                   inventory_generation, requested_action, expires_at, consumed_at
              FROM PermalinkConfirmationChallenges WHERE {predicate}
            """;
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var operationId = Guid.Parse(reader.GetString(1));
        return new ChallengeRow(
            Guid.Parse(reader.GetString(0)),
            operationId,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseDate(reader.GetString(6), operationId),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : ParseDate(reader.GetString(7), operationId));
    }

    private static async Task<T> GuardStoreAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "confirmation-challenge-store-failure",
                $"The confirmation challenge store failed closed ({exception.GetType().Name}: {exception.Message}).",
                exception);
        }
    }

    private static PermalinkException Conflict(string code, string message)
        => new(PermalinkErrorKind.Conflict, code, message);

    private sealed record ChallengeRow(
        Guid ChallengeId,
        Guid OperationId,
        string ChallengeDigest,
        string TargetDigest,
        string InventoryGeneration,
        string RequestedAction,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt);
}
