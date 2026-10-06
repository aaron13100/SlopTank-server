// SlopTank modification notice: added or changed by SlopTank on 2026-10-06.
using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Performs validated, transaction-committed file-level transfers of SQLite databases.
/// Snapshots fold committed WAL content into the copied file, and restores replace the target
/// through SQLite without replaying stale target sidecars, unlike a bare <c>File.Copy</c> or
/// an overwrite <c>File.Move</c> over a live database file.
/// </summary>
public static class ValidatedSqliteFileTransfer
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, folding all committed
    /// WAL content into a single integrity-checked file that is installed atomically at the destination.
    /// </summary>
    /// <param name="sourcePath">The SQLite database to snapshot.</param>
    /// <param name="destinationPath">The file to install the snapshot at.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static void WriteSnapshot(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var stagingFile = CreateStagingPath(destinationPath);
        try
        {
            CreateValidatedSnapshot(sourcePath, stagingFile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FlushFile(stagingFile);
            File.Move(stagingFile, destinationPath);
        }
        finally
        {
            DeleteDatabaseFiles(stagingFile);
        }
    }

    /// <summary>
    /// Restores the database at <paramref name="sourcePath"/> over the one at <paramref name="targetPath"/>
    /// using the same validated, transaction-committed transfer, so the target is never replaced by a bare
    /// file overwrite that could destroy it or replay a stale WAL or SHM sidecar over it.
    /// </summary>
    /// <param name="sourcePath">The SQLite database backup to restore.</param>
    /// <param name="targetPath">The SQLite database to replace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static void RestoreOver(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The SQLite database backup to restore does not exist.", sourcePath);
        }

        var stagingFile = CreateStagingPath(targetPath);
        try
        {
            CreateValidatedSnapshot(sourcePath, stagingFile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FlushFile(stagingFile);

            // Restore is only called while startup migration work is quiesced. Pool clearing
            // prevents a later borrower from retaining the previous database generation.
            SqliteConnection.ClearAllPools();

            // Commit through SQLite so a failed backup rolls back without detaching the live
            // database's WAL, SHM, or rollback journal. Once this transaction begins, finish
            // it without observing cancellation.
            CreateValidatedSnapshot(stagingFile, targetPath, CancellationToken.None);
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            DeleteDatabaseFiles(stagingFile);
        }
    }

    private static string CreateStagingPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The SQLite destination path has no parent directory.");
        return Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.staging");
    }

    private static void CreateValidatedSnapshot(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using (var source = new SqliteConnection(sourceConnectionString))
        using (var destination = new SqliteConnection(destinationConnectionString))
        {
            source.Open();
            destination.Open();
            cancellationToken.ThrowIfCancellationRequested();
            source.BackupDatabase(destination);
        }

        ValidateDatabase(destinationPath);
    }

    private static void ValidateDatabase(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();

        var resultCount = 0;
        while (reader.Read())
        {
            resultCount++;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The staged SQLite database failed its integrity check.");
            }
        }

        if (resultCount != 1)
        {
            throw new InvalidDataException("The staged SQLite database returned an unexpected integrity-check result.");
        }
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteDatabaseSidecars(string path)
    {
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path + "-journal");
    }

    private static void DeleteDatabaseFiles(string path)
    {
        File.Delete(path);
        DeleteDatabaseSidecars(path);
    }
}
