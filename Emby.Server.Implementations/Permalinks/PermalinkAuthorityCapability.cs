// SlopTank modification notice: added or changed by SlopTank on 2026-09-19, 2026-10-14.
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Provides connection-local, MAC-bound authority write capabilities.</summary>
internal sealed class PermalinkAuthorityCapability
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<SqliteConnection, Scope> _scopes = new();

    /// <summary>Registers the non-SQLite capability validator on a server connection.</summary>
    /// <param name="connection">The newly opened server connection.</param>
    public void Register(SqliteConnection connection)
    {
        connection.CreateFunction("authority_counter_write", () => 1L);
        connection.CreateFunction<byte[]?, byte[]?, byte[]?, byte[]?, long>(
            "authority_capability",
            (table, verb, primaryKey, presentedMac) =>
            {
                if (table is null
                    || verb is null
                    || primaryKey is null
                    || presentedMac is null
                    || !_scopes.TryGetValue(connection, out var scope)
                    || !ExactBytesEqual(scope.TableBytes, table)
                    || !ExactBytesEqual(scope.VerbBytes, verb)
                    || !ExactBytesEqual(scope.PrimaryKeyBytes, primaryKey)
                    || !ExactBytesEqual(scope.MacBytes, presentedMac))
                {
                    return 0;
                }

                return 1;
            });
        connection.StateChange += (sender, args) =>
        {
            if (args.CurrentState == System.Data.ConnectionState.Closed)
            {
                _scopes.TryRemove(connection, out _);
            }
        };
    }

    /// <summary>Begins one exact write capability scope for a server transaction.</summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="table">The protected table.</param>
    /// <param name="verb">The SQL verb.</param>
    /// <param name="primaryKey">The canonical primary-key text.</param>
    /// <param name="epoch">The authority protocol epoch.</param>
    /// <returns>A scope carrying the MAC that the authorization row stores.</returns>
    public PermalinkAuthorityWriteScope Begin(
        SqliteConnection connection,
        Guid operationId,
        string table,
        string verb,
        string primaryKey,
        long epoch)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(primaryKey);
        var tableBytes = StrictUtf8.GetBytes(table);
        var verbBytes = StrictUtf8.GetBytes(verb);
        var primaryKeyBytes = StrictUtf8.GetBytes(primaryKey);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var mac = Mac(operationId, table, verb, primaryKey, epoch, nonce);
        var scope = new Scope(
            operationId,
            table,
            tableBytes,
            verb,
            verbBytes,
            primaryKey,
            primaryKeyBytes,
            Encoding.ASCII.GetBytes(mac),
            epoch,
            nonce);
        var writeScope = new PermalinkAuthorityWriteScope(
            mac,
            nonce,
            () => _scopes.TryRemove(connection, out _));
        if (!_scopes.TryAdd(connection, scope))
        {
            throw new InvalidOperationException("The authority connection already has a write capability.");
        }

        return writeScope;
    }

    private static bool ExactBytesEqual(byte[] expected, byte[] actual)
        => expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);

    private string Mac(
        Guid operationId,
        string table,
        string verb,
        string primaryKey,
        long epoch,
        string nonce)
    {
        var message = Encoding.UTF8.GetBytes(
            $"{operationId:D}\0{table}\0{verb}\0{primaryKey}\0{epoch}\0{nonce}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_secret, message));
    }

    private sealed record Scope(
        Guid OperationId,
        string Table,
        byte[] TableBytes,
        string Verb,
        byte[] VerbBytes,
        string PrimaryKey,
        byte[] PrimaryKeyBytes,
        byte[] MacBytes,
        long Epoch,
        string Nonce);
}
