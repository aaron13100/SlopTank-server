// SlopTank modification notice: added or changed by SlopTank on 2026-09-24, 2026-10-03, 2026-10-05.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Configures jellyfin to use an SQLite database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-SQLite")]
public sealed class SqliteDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "SQLiteBackups";
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<SqliteDatabaseProvider> _logger;
    private string? _databasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the fallback when the old data path configuration is used.</param>
    /// <param name="logger">A logger.</param>
    public SqliteDatabaseProvider(IApplicationPaths applicationPaths, ILogger<SqliteDatabaseProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        static T? GetOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
        {
            if (options is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            var value = options.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            return converter(value.Value);
        }

        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var sqliteConnectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = GetOption(customOptions, "path", e => e, () => Path.Combine(_applicationPaths.DataPath, "jellyfin.db")),
            Cache = GetOption(customOptions, "cache", Enum.Parse<SqliteCacheMode>, () => SqliteCacheMode.Default),
            Pooling = GetOption(customOptions, "pooling", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => true),
            DefaultTimeout = GetOption(customOptions, "command-timeout", int.Parse, () => 60)
        };

        _databasePath = Path.GetFullPath(sqliteConnectionBuilder.DataSource!);

        var connectionString = sqliteConnectionBuilder.ToString();

        // Log SQLite connection parameters
        _logger.LogInformation("SQLite connection string: {ConnectionString}", connectionString);

        options
            .UseSqlite(
                connectionString,
                sqLiteOptions => sqLiteOptions.MigrationsAssembly(GetType().Assembly))
            // TODO: Remove when https://github.com/dotnet/efcore/pull/35873 is merged & released
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning)
                    .Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .AddInterceptors(
                new PragmaConnectionInterceptor(
                    _logger,
                    GetOption<int?>(customOptions, "cacheSize", e => int.Parse(e, CultureInfo.InvariantCulture)),
                    GetOption(customOptions, "lockingmode", e => e, () => "NORMAL")!,
                    GetOption(customOptions, "journalsizelimit", int.Parse, () => 134_217_728),
                    GetOption(customOptions, "tempstoremode", int.Parse, () => 2),
                    GetOption(customOptions, "syncmode", int.Parse, () => 1),
                    customOptions?.Where(e => e.Key.StartsWith("#PRAGMA:", StringComparison.OrdinalIgnoreCase)).ToDictionary(e => e.Key["#PRAGMA:".Length..], e => e.Value) ?? []),
                new SqlitePooledHandleGuardInterceptor(_logger));

        var enableSensitiveDataLogging = GetOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(enableSensitiveDataLogging);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on SQLite connection");
        }
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        var context = await DbContextFactory!.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA optimize", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("jellyfin.db optimized successfully!");
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.SetDefaultDateTimeKind(DateTimeKind.Utc);
    }

    /// <inheritdoc/>
    public async Task RunShutdownTask(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        // Run before disposing the application
        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA optimize", cancellationToken).ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new DoNotUseReturningClauseConvention());
    }

    /// <inheritdoc />
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        var path = GetDatabasePath();
        var backupDirectory = GetBackupDirectory(path);
        Directory.CreateDirectory(backupDirectory);

        var backupFile = GetBackupPath(backupDirectory, key);
        var stagingFile = CreateStagingPath(backupFile);
        try
        {
            CreateValidatedSnapshot(path, stagingFile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FlushFile(stagingFile);
            File.Move(stagingFile, backupFile);
            return Task.FromResult(key);
        }
        finally
        {
            DeleteDatabaseFiles(stagingFile);
        }
    }

    /// <inheritdoc />
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetDatabasePath();
        var backupFile = GetBackupPath(GetBackupDirectory(path), key);

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            throw new FileNotFoundException("The SQLite migration backup does not exist.", backupFile);
        }

        var stagingFile = CreateStagingPath(path);
        try
        {
            CreateValidatedSnapshot(backupFile, stagingFile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FlushFile(stagingFile);

            // Restore is only called while startup migration work is quiesced. Pool clearing
            // prevents a later borrower from retaining the previous database generation.
            SqliteConnection.ClearAllPools();

            // Commit through SQLite so a failed backup rolls back without detaching the live
            // database's WAL, SHM, or rollback journal. Once this transaction begins, finish
            // it without observing cancellation.
            CreateValidatedSnapshot(stagingFile, path, CancellationToken.None);
            SqliteConnection.ClearAllPools();
            return Task.CompletedTask;
        }
        finally
        {
            DeleteDatabaseFiles(stagingFile);
        }
    }

    /// <inheritdoc />
    public Task DeleteBackup(string key)
    {
        var path = GetDatabasePath();
        var backupFile = GetBackupPath(GetBackupDirectory(path), key);

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        return Task.CompletedTask;
    }

    private static string GetBackupDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The SQLite database path has no parent directory.");
        return Path.Combine(directory, BackupFolderName);
    }

    private static string GetBackupPath(string backupDirectory, string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || key.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || key.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The SQLite backup key is not a valid file name.", nameof(key));
        }

        return Path.Combine(backupDirectory, $"{key}_jellyfin.db");
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

    private string GetDatabasePath()
        => _databasePath ?? Path.GetFullPath(Path.Combine(_applicationPaths.DataPath, "jellyfin.db"));

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var deleteQueries = new List<string>();
        foreach (var tableName in tableNames)
        {
            deleteQueries.Add($"DELETE FROM \"{tableName}\";");
        }

        var deleteAllQuery =
        $"""
        PRAGMA foreign_keys = OFF;
        {string.Join('\n', deleteQueries)}
        PRAGMA foreign_keys = ON;
        """;

        await dbContext.Database.ExecuteSqlRawAsync(deleteAllQuery).ConfigureAwait(false);
    }
}
