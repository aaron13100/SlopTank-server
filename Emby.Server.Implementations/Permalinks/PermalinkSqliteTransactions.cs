// SlopTank modification notice: added or changed by SlopTank on 2026-10-07, 2026-10-10.
using System;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Creates SQLite transactions whose locking and isolation semantics are both explicit.</summary>
internal static class PermalinkSqliteTransactions
{
    /// <summary>
    /// Begins a deferred serializable transaction through Microsoft.Data.Sqlite's only API that
    /// can express both requirements. Version 10.0.10 has no asynchronous overload carrying the
    /// deferred flag; its inherited asynchronous API maps read-uncommitted to deferred instead.
    /// Transaction construction is therefore kept in this small synchronous provider boundary.
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <returns>The deferred serializable transaction.</returns>
    public static SqliteTransaction BeginDeferredSerializable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
    }

    /// <summary>
    /// Begins an immediate serializable transaction. Acquiring the SQLite writer reservation
    /// before reading makes a read-then-update consume decision atomic across connections.
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <returns>The immediate serializable transaction.</returns>
    public static SqliteTransaction BeginImmediateSerializable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
    }
}
