namespace SocialGraph.Api.Migrations;

using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class DatabaseMigrationOptions
{
    public const string SectionName = "DatabaseMigrations";

    public bool Enabled { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;
}

public sealed class SocialGraphDatabaseMigrationHostedService(
    SocialGraphDatabaseMigrator migrator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        migrator.MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SocialGraphDatabaseMigrator(
    IConfiguration configuration,
    IOptions<DatabaseMigrationOptions> options,
    ILogger<SocialGraphDatabaseMigrator> logger)
{
    private const int AdvisoryLockNamespace = 0x46414B45; // "FAKE"
    private const int AdvisoryLockService = 0x534F4347; // "SOCG"
    private const string HistoryTable = "social_graph.schema_migrations";

    private static readonly Assembly MigrationAssembly = typeof(SocialGraphDatabaseMigrator).Assembly;

    private static readonly MigrationDefinition[] Migrations =
    [
        new("00000000_schema", "SocialGraph.Api.Migrations.Sql.00000000_schema.sql"),
        new("20260727_add_hot_path_indexes", "SocialGraph.Api.Migrations.Sql.20260727_add_hot_path_indexes.sql"),
        new("20260802_create_integration_outbox", "SocialGraph.Api.Migrations.Sql.20260802_create_integration_outbox.sql")
    ];

    internal static IReadOnlyList<string> MigrationVersions =>
        Migrations.Select(migration => migration.Version).ToArray();

    internal static IReadOnlyList<string> MigrationResourceNames =>
        Migrations.Select(migration => migration.ResourceName).ToArray();

    private readonly DatabaseMigrationOptions _options = options.Value;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "Automatic SocialGraph database migrations are disabled. The database must be migrated before this instance starts serving traffic.");
            return;
        }

        var connectionString = configuration.GetConnectionString("PostgreSQLMigration");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString("PostgreSQL");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PostgreSQLMigration or ConnectionStrings:PostgreSQL is required when automatic database migrations are enabled.");
        }

        var migrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            ApplicationName = "fakebook-socialgraph-migrations"
        };
        await using var connection = new NpgsqlConnection(migrationConnection.ConnectionString);
        var lockHeld = false;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SetMigrationLockAsync(connection, acquire: true, cancellationToken);
            lockHeld = true;

            await EnsureHistoryTableAsync(connection, cancellationToken);
            foreach (var migration in Migrations)
            {
                await ApplyAsync(connection, migration, cancellationToken);
            }

            logger.LogInformation("SocialGraph database migrations are current.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "SocialGraph database migration failed; startup is aborted.");
            throw;
        }
        finally
        {
            if (lockHeld && connection.State == ConnectionState.Open)
            {
                try
                {
                    await SetMigrationLockAsync(connection, acquire: false, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // Closing the physical connection below releases every session advisory
                    // lock even if an explicit unlock cannot be confirmed.
                    logger.LogWarning(exception, "Could not explicitly release the SocialGraph migration advisory lock; closing the connection will release it.");
                }
            }
        }
    }

    private async Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            CREATE SCHEMA IF NOT EXISTS social_graph;
            CREATE TABLE IF NOT EXISTS social_graph.schema_migrations (
                version varchar(200) PRIMARY KEY,
                checksum char(64) NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyAsync(
        NpgsqlConnection connection,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        var sql = await ReadMigrationSqlAsync(migration, cancellationToken);
        var checksum = ComputeChecksum(sql);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var installedChecksum = await GetInstalledChecksumAsync(
            connection,
            transaction,
            migration.Version,
            cancellationToken);
        if (installedChecksum is not null)
        {
            if (!ChecksumsMatch(installedChecksum, checksum))
            {
                throw new InvalidOperationException(
                    $"Database migration '{migration.Version}' was changed after it was applied. " +
                    $"Stored checksum '{installedChecksum.Trim()}', embedded checksum '{checksum}'.");
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogDebug("Database migration {Migration} is already applied.", migration.Version);
            return;
        }

        await using (var migrationCommand = CreateCommand(connection, transaction, sql))
        {
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var historyCommand = CreateCommand(
            connection,
            transaction,
            $"INSERT INTO {HistoryTable} (version, checksum, applied_at) VALUES (@version, @checksum, now());"))
        {
            historyCommand.Parameters.AddWithValue("version", migration.Version);
            historyCommand.Parameters.AddWithValue("checksum", checksum);
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Applied SocialGraph database migration {Migration}.", migration.Version);
    }

    private async Task<string?> GetInstalledChecksumAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"SELECT checksum FROM {HistoryTable} WHERE version = @version;");
        command.Parameters.AddWithValue("version", version);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task SetMigrationLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        CancellationToken cancellationToken)
    {
        var function = acquire ? "pg_advisory_lock" : "pg_advisory_unlock";
        await using var command = CreateCommand(
            connection,
            transaction: null,
            $"SELECT {function}(@lockNamespace, @lockService);");
        command.Parameters.AddWithValue("lockNamespace", AdvisoryLockNamespace);
        command.Parameters.AddWithValue("lockService", AdvisoryLockService);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!acquire && result is not true)
        {
            throw new InvalidOperationException("The SocialGraph migration advisory lock was not held by this database session.");
        }
    }

    private NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText) =>
        new(commandText, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds
        };

    private static async Task<string> ReadMigrationSqlAsync(
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        await using var stream = MigrationAssembly.GetManifestResourceStream(migration.ResourceName) ??
            throw new InvalidOperationException(
                $"Embedded migration resource '{migration.ResourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    internal static string ComputeChecksum(string value)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    internal static bool ChecksumsMatch(string installedChecksum, string expectedChecksum) =>
        string.Equals(
            installedChecksum.Trim(),
            expectedChecksum,
            StringComparison.OrdinalIgnoreCase);

    private sealed record MigrationDefinition(string Version, string ResourceName);
}
