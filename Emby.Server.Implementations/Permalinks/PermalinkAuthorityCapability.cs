// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Provides connection-local, MAC-bound authority write capabilities.</summary>
internal sealed class PermalinkAuthorityCapability
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<SqliteConnection, Scope> _scopes = new();

    /// <summary>Registers the non-SQLite capability validator on a server connection.</summary>
    /// <param name="connection">The newly opened server connection.</param>
    public void Register(SqliteConnection connection)
    {
        connection.CreateFunction("authority_counter_write", () => 1L);
        connection.CreateFunction<string, string, string, string, long>(
            "authority_capability",
            (table, verb, primaryKey, presentedMac) =>
            {
                if (!_scopes.TryGetValue(connection, out var scope)
                    || !string.Equals(scope.Table, table, StringComparison.Ordinal)
                    || !string.Equals(scope.Verb, verb, StringComparison.Ordinal)
                    || !string.Equals(scope.PrimaryKey, primaryKey, StringComparison.Ordinal))
                {
                    return 0;
                }

                var expected = Mac(
                    scope.OperationId,
                    table,
                    verb,
                    primaryKey,
                    scope.Epoch,
                    scope.Nonce);
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(presentedMac)) ? 1 : 0;
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
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var scope = new Scope(operationId, table, verb, primaryKey, epoch, nonce);
        if (!_scopes.TryAdd(connection, scope))
        {
            throw new InvalidOperationException("The authority connection already has a write capability.");
        }

        return new PermalinkAuthorityWriteScope(
            Mac(operationId, table, verb, primaryKey, epoch, nonce),
            nonce,
            () => _scopes.TryRemove(connection, out _));
    }

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
        string Verb,
        string PrimaryKey,
        long Epoch,
        string Nonce);
}
