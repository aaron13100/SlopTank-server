// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-09.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns transactional alias/content elections and issuance verification.
/// </summary>
internal sealed class PermalinkTransitionStore
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private readonly PermalinkAuthorityStore _authority;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkTransitionStore"/> class.
    /// </summary>
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="fileSystem">The durable permalink filesystem.</param>
    /// <param name="timeProvider">The time provider.</param>
    public PermalinkTransitionStore(
        PermalinkAuthorityStore authority,
        IPermalinkAtomicFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Allocates and durably publishes one never-reusable fallback id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkIssuance> AllocateAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GenerateId();
            var nonce = Guid.NewGuid();
            var createdAt = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            var issuance = new PermalinkIssuance(id, nonce, createdAt);
            try
            {
                await _authority.PublishIssuanceAsync(
                    id + ".json",
                    CanonicalJson.Serialize(issuance),
                    cancellationToken).ConfigureAwait(false);
                return issuance;
            }
            catch (PermalinkException exception)
                when (exception.Code == "publish-exclusive")
            {
                continue;
            }
        }

        throw new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "allocation-exhausted",
            "Permalink allocation exhausted 32 create-exclusive retries.");
    }

    /// <summary>
    /// Elects one first fallback alias for a seeded-unpublished capsule.
    /// </summary>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="issuance">The issuance.</param>
    /// <param name="aliasEvent">The alias event.</param>
    /// <param name="eventJson">The event json.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkAliasClaim> ClaimFirstAliasAsync(
        Guid capsuleId,
        PermalinkIssuance issuance,
        PermalinkEventDocument aliasEvent,
        ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken)
    {
        return await ClaimAliasAsync(
            capsuleId,
            issuance.Id,
            issuance.IssuanceNonce,
            issuance.CreatedAt,
            aliasEvent,
            eventJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Elects one accepted provider alias without allocating a fallback issuance.
    /// </summary>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="alias">The alias.</param>
    /// <param name="aliasEvent">The alias event.</param>
    /// <param name="eventJson">The event json.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkAliasClaim> ClaimExternalAliasAsync(
        Guid capsuleId,
        string alias,
        PermalinkEventDocument aliasEvent,
        ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken)
    {
        return await ClaimAliasAsync(
            capsuleId,
            alias,
            null,
            aliasEvent.CreatedAt,
            aliasEvent,
            eventJson,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PermalinkAliasClaim> ClaimAliasAsync(
        Guid capsuleId,
        string alias,
        Guid? issuanceNonce,
        string createdAt,
        PermalinkEventDocument aliasEvent,
        ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var digest = CanonicalJson.Digest(eventJson);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO FirstAliasClaims
                    (capsule_id, permalink_id, issuance_nonce, alias_event_id,
                     event_digest, event_json, created_at)
                VALUES ($capsule, $id, $nonce, $event, $digest, $json, $created)
                """;
            insert.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
            insert.Parameters.AddWithValue("$id", alias);
            insert.Parameters.AddWithValue(
                "$nonce",
                issuanceNonce?.ToString("D") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$event", aliasEvent.EventId.ToString("D"));
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(eventJson.Span));
            insert.Parameters.AddWithValue("$created", createdAt);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT permalink_id, issuance_nonce, alias_event_id, event_json, created_at
              FROM FirstAliasClaims
             WHERE capsule_id = $capsule
            """;
        select.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var id = reader.GetString(0);
        Guid? nonce = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? null
            : Guid.Parse(reader.GetString(1));
        var eventId = Guid.Parse(reader.GetString(2));
        var json = Encoding.UTF8.GetBytes(reader.GetString(3));
        var selectedCreatedAt = reader.GetString(4);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (nonce.HasValue)
        {
            await using var issuanceInsert = connection.CreateCommand();
            issuanceInsert.Transaction = transaction;
            issuanceInsert.CommandText = """
                INSERT OR IGNORE INTO PermalinkIssuances
                    (PermalinkId, IssuanceNonce, CreatedAt, created_at)
                VALUES ($id, $nonce, $created, $created)
                """;
            issuanceInsert.Parameters.AddWithValue("$id", id);
            issuanceInsert.Parameters.AddWithValue("$nonce", nonce.Value.ToString("D"));
            issuanceInsert.Parameters.AddWithValue("$created", selectedCreatedAt);
            _ = await issuanceInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PermalinkAliasClaim(
            id,
            nonce,
            eventId,
            json,
            string.Equals(id, alias, StringComparison.Ordinal)
                && Nullable.Equals(nonce, issuanceNonce));
    }

    /// <summary>
    /// Returns the authority-elected ordered alias set for a capsule.
    /// </summary>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<IReadOnlyList<string>> GetAliasesAsync(
        Guid capsuleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT permalink_id
              FROM FirstAliasClaims
             WHERE capsule_id = $capsule AND permalink_id IS NOT NULL
             ORDER BY created_at, permalink_id
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        var aliases = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            aliases.Add(reader.GetString(0));
        }

        return aliases;
    }

    /// <summary>
    /// Validates an immutable issuance tombstone against its elected nonce.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="nonce">The nonce.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ValidateIssuanceAsync(
        string id,
        Guid nonce,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_authority.IssuedRoot, id + ".json");
        if (!File.Exists(path))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "issuance-missing",
                $"Permalink issuance '{id}' is missing at '{path}'.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var issuance = CanonicalJson.Deserialize<PermalinkIssuance>(bytes, path);
        if (issuance.Type != "sloptank.permalink-issuance"
            || issuance.Version != 1
            || !string.Equals(issuance.Id, id, StringComparison.Ordinal)
            || !issuance.IssuanceNonce.Equals(nonce))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "issuance-foreign",
                $"Permalink issuance '{path}' is foreign or has unknown identity fields.");
        }
    }

    /// <summary>
    /// Permanently claims one strict-superset content successor.
    /// </summary>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="predecessorEventId">The predecessor event id.</param>
    /// <param name="successor">The successor.</param>
    /// <param name="eventJson">The event json.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AuthorizeContentTransitionAsync(
        Guid capsuleId,
        Guid predecessorEventId,
        PermalinkEventDocument successor,
        ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var digest = CanonicalJson.Digest(eventJson);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ContentTransitionClaims
                (capsule_id, predecessor, operation_id, event_id,
                 event_digest, created_at)
            VALUES ($capsule, $predecessor, $operation, $event, $digest, $created);
            INSERT INTO OperationDecisions
                (operation_id, capsule_id, predecessor, decision,
                 event_id, event_digest, created_at)
            VALUES ($operation, $capsule, $predecessor, 'ready',
                    $event, $digest, $created);
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        command.Parameters.AddWithValue("$predecessor", predecessorEventId.ToString("D"));
        command.Parameters.AddWithValue("$operation", successor.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$event", successor.EventId.ToString("D"));
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$created", successor.CreatedAt);
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "content-transition-conflict",
                $"Content predecessor '{predecessorEventId}' already has a claimed successor.",
                exception);
        }
    }

    private static string GenerateId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        Span<char> encoded = stackalloc char[26];
        for (var index = 25; index >= 0; index--)
        {
            encoded[index] = Alphabet[(int)(low & 31)];
            low = (low >> 5) | (high << 59);
            high >>= 5;
        }

        return "sk-" + new string(encoded);
    }
}
