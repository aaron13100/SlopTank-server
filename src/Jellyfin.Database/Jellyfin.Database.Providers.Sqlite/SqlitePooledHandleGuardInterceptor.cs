// SlopTank modification notice: added or changed by SlopTank on 2026-09-24.
// This file is new: added to close the pooled-handle collation race that
// refused the 2026-09-11T11:38Z promotion ("SQLite Error 5: 'unable to
// delete/modify collation sequence due to active statements'"). Upstream
// reports without a fix: jellyfin/jellyfin #14614, #15149; dotnet/efcore
// #36591, #37668, #32512, #24353 (all closed "not planned").
using System;
using System.Collections;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Removes EF Core's per-connection SQLite helper registrations so that pooled
/// native handles never run the re-register/unregister cycle that SQLite rejects
/// with SQLITE_BUSY ("unable to delete/modify collation sequence due to active
/// statements", or the user-function twin) whenever a statement is still active
/// on the handle.
/// </summary>
/// <remarks>
/// <para>
/// EF Core registers the EF_DECIMAL collation, the scalar functions regexp and
/// ef_mod/ef_add/ef_divide/ef_compare/ef_multiply/ef_negate, and the aggregates
/// ef_avg/ef_max/ef_min/ef_sum on every <see cref="SqliteConnection"/> it opens
/// (SqliteRelationalConnection.InitializeDbConnection), and Microsoft.Data.Sqlite
/// records them in per-connection dictionaries. Pooling then re-applies the
/// recorded helpers whenever the native handle is reactivated
/// (<see cref="SqliteConnection.Open()"/>) and unregisters them when it is
/// returned to the pool (Deactivate). SQLite refuses to replace or delete a
/// helper that still carries a live callback while the handle has an active
/// (stepped, unreset) statement, so any handle that reaches a pool boundary
/// with a live statement turns that boundary into the promotion gate's exact
/// error and poisons requests that own neither the statement nor the
/// registration.
/// </para>
/// <para>
/// The interceptor deletes the helper surface from the open native handle
/// immediately (exact arities, via the public SQLitePCLRaw handle API) and
/// clears Microsoft.Data.Sqlite's per-connection tracking dictionaries, so both
/// pool boundaries replay nothing from then on. The helper surface is dead
/// weight on this deployment: the Jellyfin model has no decimal columns and no
/// generated SQL references these helpers (verified by sweep), so removing them
/// changes no executed query. Public null-callback re-registration would cover
/// the collation and scalar functions, but no public overload deletes an
/// aggregate at its registered arity without storing a live final selector,
/// which is why the dictionary clear is reflection-based.
/// </para>
/// <para>
/// The deterministic regression guard for the whole mechanism, including EF or
/// Microsoft.Data.Sqlite drift that this reflection misses, is
/// PooledHandleCollationPoisonTests in the integration suite: if any tracked
/// helper survives neutralization, the close-with-active-statement sequence
/// fails there with the gate error again.
/// </para>
/// <para>
/// This interceptor is only safe while (a) no entity property in the EF model maps a
/// decimal CLR type and (b) no LINQ query translates to the regexp or ef_* helpers it
/// deletes. PooledHandleGuardModelAssumptionsTests in the unit test suite guards both:
/// it asserts the model has no decimal properties (with a negative control proving that
/// check can fail) and demonstrates, with a throwaway regexp-translated query, the exact
/// SqliteException a real query would hit if assumption (b) ever stopped holding. Revisit
/// this interceptor if either test starts failing.
/// </para>
/// </remarks>
public sealed class SqlitePooledHandleGuardInterceptor : DbConnectionInterceptor
{
    private const string CollationName = "EF_DECIMAL";

    private static readonly (string Name, int Arity)[] _functions =
    [
        ("regexp", 2),
        ("ef_mod", 2),
        ("ef_add", 2),
        ("ef_divide", 2),
        ("ef_compare", 2),
        ("ef_multiply", 2),
        ("ef_negate", 1),
        ("ef_avg", 1),
        ("ef_max", 1),
        ("ef_min", 1),
        ("ef_sum", 1)
    ];

    private static readonly string[] _trackedDictionaries = ["_collations", "_functions", "_aggregates"];

    private readonly ILogger _logger;
    private int _dictionaryClearFailedLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlitePooledHandleGuardInterceptor"/> class.
    /// </summary>
    /// <param name="logger">The logger used to surface a failed dictionary clear.</param>
    public SqlitePooledHandleGuardInterceptor(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        NeutralizeTrackedHelpers((SqliteConnection)connection);
    }

    /// <inheritdoc/>
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
        NeutralizeTrackedHelpers((SqliteConnection)connection);
    }

    private void NeutralizeTrackedHelpers(SqliteConnection connection)
    {
        var handle = connection.Handle;
        if (handle is null)
        {
            return;
        }

        // Delete EF's helper registrations from the native handle now, while the
        // connection is freshly activated and quiet. Every later pool-boundary
        // replay then finds nothing to replace or delete and cannot trip
        // SQLite's active-statement guard (verified by probe: null deletions
        // over an active statement return SQLITE_OK).
        var rc = raw.sqlite3_create_collation(handle, CollationName, null, null);
        if (rc != 0)
        {
            _logger.LogError(
                "Removing the {Collation} collation from a pooled SQLite handle returned {Rc}",
                CollationName,
                rc);
        }

        foreach (var (name, arity) in _functions)
        {
            rc = raw.sqlite3_create_function(handle, name, arity, null, null);
            if (rc != 0)
            {
                _logger.LogError(
                    "Removing the {Function} function (arity {Arity}) from a pooled SQLite handle returned {Rc}",
                    name,
                    arity,
                    rc);
            }
        }

        ClearTrackedHelpers(connection);
    }

    private void ClearTrackedHelpers(SqliteConnection connection)
    {
        // Microsoft.Data.Sqlite re-applies whatever these dictionaries hold at
        // every pooled activation and unregister at every pool return; clearing
        // them is what turns both boundaries into no-ops. On a Microsoft.Data
        // .Sqlite version that renames them, log once and keep going: the
        // surviving replay is caught by PooledHandleCollationPoisonTests.
        foreach (var fieldName in _trackedDictionaries)
        {
            try
            {
                var field = typeof(SqliteConnection).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field?.GetValue(connection) is IDictionary tracked && tracked.Count > 0)
                {
                    tracked.Clear();
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref _dictionaryClearFailedLogged, 1, 0) == 0)
                {
                    _logger.LogError(
                        exception,
                        "Could not clear Microsoft.Data.Sqlite tracked helper dictionary '{Field}' on a pooled connection; the pooled-handle collation race remains possible",
                        fieldName);
                }
            }
        }
    }
}
