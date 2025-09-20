using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Configures jellyfin to use an Postgres database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PgSql")]
public sealed class PgSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "PgsqlBackups";
    private readonly ILogger<PgSqlDatabaseProvider> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILoggerFactory _loggerFactory;
    private IEntityFrameworkCoreLockingBehavior? _lockingBehavior;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the backup paths.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public PgSqlDatabaseProvider(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _loggerFactory = loggerFactory;
        _logger = _loggerFactory.CreateLogger<PgSqlDatabaseProvider>();
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public IEntityFrameworkCoreLockingBehavior LockingBehavior =>
        _lockingBehavior ?? CreateLockingBehavior(DatabaseLockingBehaviorTypes.NoLock);

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        _lockingBehavior = CreateLockingBehavior(databaseConfiguration.LockingBehavior);
        _lockingBehavior.Initialise(options);

        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var connectionBuilder = GetConnectionBuilder(customOptions);
        connectionBuilder.ApplicationName = $"jellyfin+{FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly()!.Location).FileVersion}";

        options
            .UseNpgsql(connectionBuilder.ToString(), pgSqlOptions =>
            {
                pgSqlOptions.MigrationsAssembly(GetType().Assembly.FullName);
                pgSqlOptions.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies));
            });

        var enableSensitiveDataLogging = GetCustomDatabaseOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(enableSensitiveDataLogging);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on PostgreSQL connection");
        }
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (context.Database.IsNpgsql())
            {
                await context.Database.ExecuteSqlRawAsync("VACUUM ANALYZE", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("PostgreSQL database optimized successfully");
            }
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Use C collation for consistent case-sensitive behavior matching SQLite BINARY
        modelBuilder.UseCollation("C");

        // Configure all DateTime properties to ensure UTC for PostgreSQL compatibility, matching SQLite provider
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(
                        new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                            v => v.ToUniversalTime(),
                            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        // Clear Npgsql connection pools on shutdown
        NpgsqlConnection.ClearAllPools();
        _logger.LogInformation("PostgreSQL connection pools cleared on shutdown");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupFolder = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        Directory.CreateDirectory(backupFolder);

        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(backupFolder, $"{key}_{connectionBuilder.Database}.sql");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --file=\"{backupFile}\" --no-password --verbose --clean --if-exists",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL backup: {BackupFile}", backupFile);

        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("pg_dump failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"pg_dump failed: {error}");
        }

        _logger.LogInformation("PostgreSQL backup completed successfully: {BackupFile}", backupFile);
        return key;
    }

    /// <inheritdoc/>
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();

        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "psql",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --file=\"{backupFile}\" --no-password --quiet",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL restore from: {BackupFile}", backupFile);

        process.Start();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("psql restore failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"psql restore failed: {error}");
        }

        _logger.LogInformation("PostgreSQL restore completed successfully from: {BackupFile}", backupFile);
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        var connectionBuilder = GetConnectionBuilder(null);
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        _logger.LogInformation("Deleted backup file: {BackupFile}", backupFile);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var truncateQueries = new List<string>();
        foreach (var tableName in tableNames)
        {
            truncateQueries.Add($"TRUNCATE TABLE \"{tableName}\" RESTART IDENTITY CASCADE;");
        }

        var truncateAllQuery = string.Join('\n', truncateQueries);

        await dbContext.Database.ExecuteSqlRawAsync(truncateAllQuery).ConfigureAwait(false);
        _logger.LogInformation("PostgreSQL database tables purged successfully");
    }

    private IEntityFrameworkCoreLockingBehavior CreateLockingBehavior(DatabaseLockingBehaviorTypes lockingBehaviorType)
    {
        return lockingBehaviorType switch
        {
            DatabaseLockingBehaviorTypes.NoLock => new NoLockBehavior(_loggerFactory),
            DatabaseLockingBehaviorTypes.Optimistic => new OptimisticLockBehavior(
                medianFirstRetryDelay: TimeSpan.FromMilliseconds(150),
                maxDelay: TimeSpan.FromSeconds(5),
                maxRetries: 15,
                shouldRetry: ShouldRetry,
                loggerFactory: _loggerFactory),
            DatabaseLockingBehaviorTypes.Pessimistic => throw new NotSupportedException("Pessimistic locking is not supported for PostgreSQL"),
            _ => throw new ArgumentOutOfRangeException(nameof(lockingBehaviorType), lockingBehaviorType, null)
        };
    }

    private T? GetCustomDatabaseOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
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

    private NpgsqlConnectionStringBuilder GetConnectionBuilder(ICollection<CustomDatabaseOption>? options)
    {
        var includeErrorDetail = GetCustomDatabaseOption(options, "IncludeErrorDetail", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        var logParameters = GetCustomDatabaseOption(options, "LogParameters", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);

        var connectionBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "jellyfin",
            Port = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432", CultureInfo.InvariantCulture),
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "jellyfin",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "jellyfin",
            Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? throw new InvalidOperationException("PostgreSQL password must be provided via POSTGRES_PASSWORD environment variable")
        };

        if (includeErrorDetail)
        {
            connectionBuilder.IncludeErrorDetail = includeErrorDetail;
        }

        if (logParameters)
        {
            connectionBuilder.LogParameters = logParameters;
        }

        // Log the full connection string without password
        var safeConnectionString = new NpgsqlConnectionStringBuilder(connectionBuilder.ToString())
        {
            Password = null
        }.ToString();

        _logger.LogInformation("PostgreSQL connection string: {ConnectionString}", safeConnectionString);

        return connectionBuilder;
    }

    private static bool ShouldRetry(Exception? exception)
    {
        // Handle PostgresException (server-side errors)
        if (exception is PostgresException pgEx)
        {
            return pgEx.SqlState switch
            {
            // Retry Connection Exception (08xxx)
            string code when code.StartsWith("08", StringComparison.Ordinal) => true,

            // Retry Transaction Rollback (40xxx)
            string code when code.StartsWith("40", StringComparison.Ordinal) => true,

            // Retry Insufficient Resources (53xxx)
            string code when code.StartsWith("53", StringComparison.Ordinal) => true,

            // Retry Operator Intervention (57xxx)
            string code when code.StartsWith("57", StringComparison.Ordinal) => true,

            // Retry System Error (58xxx)
            string code when code.StartsWith("58", StringComparison.Ordinal) => true,

            // Do not retry other PostgreSQL exceptions by default
            _ => false
            };
        }

        // Handle InvalidOperationException (connection issues)
        if (exception is InvalidOperationException invEx &&
            invEx.Message.Contains("Connection is not open", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
