namespace SocialGraph.Api.Migrations;

using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;

public static class AssociationContractMigrationCommand
{
    public const int LegacyVersion = 1;
    public const int CanonicalVersion = 2;
    private const string CommandName = "--migrate-association-contract";
    private const string ApplyFlag = "--apply";
    private const string SourceVersionPrefix = "--source-version=";
    private const string ContractName = "association-types";

    public static bool IsRequested(string[] args) =>
        args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var apply = args.Contains(ApplyFlag, StringComparer.OrdinalIgnoreCase);
        var sourceVersion = ParseSourceVersion(args);
        if (apply && sourceVersion != LegacyVersion)
        {
            logger.LogError(
                "Refusing to apply association migration. Pass --source-version=1 after verifying the current database uses the legacy 0..25 contract.");
            return 2;
        }

        var connectionString = configuration.GetConnectionString("PostgreSQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError("ConnectionStrings:PostgreSQL is required for the association migration command.");
            return 2;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtext('fakebook-socialgraph-association-contract'));",
            cancellationToken);

        if (!await TableExistsAsync(connection, transaction, "social_graph.associations", cancellationToken) ||
            !await TableExistsAsync(connection, transaction, "social_graph.objects", cancellationToken))
        {
            logger.LogError("social_graph.objects and social_graph.associations must exist before migration.");
            await transaction.RollbackAsync(cancellationToken);
            return 2;
        }

        var installedVersion = await GetInstalledVersionAsync(connection, transaction, cancellationToken);
        if (installedVersion >= CanonicalVersion)
        {
            logger.LogInformation("Association contract v{Version} is already installed; no changes are required.", installedVersion);
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        await BuildCanonicalProjectionAsync(connection, transaction, cancellationToken);
        var sourceRows = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM social_graph.associations;",
            cancellationToken);
        var canonicalRows = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM canonical_associations;",
            cancellationToken);
        var discardedRows = await ScalarLongAsync(
            connection,
            transaction,
            DiscardedRowsSql,
            cancellationToken);
        var resolvedConflicts = await ReadConflictCountsAsync(connection, transaction, cancellationToken);

        logger.LogInformation(
            "Association migration preview: source={SourceRows}, canonical={CanonicalRows}, discarded-invalid-or-unmapped={DiscardedRows}, mode={Mode}.",
            sourceRows,
            canonicalRows,
            discardedRows,
            apply ? "apply" : "dry-run");
        foreach (var conflict in resolvedConflicts)
        {
            logger.LogInformation(
                "Association migration normalized {Count} rows for precedence rule {Rule}.",
                conflict.Value,
                conflict.Key);
        }

        if (!apply)
        {
            logger.LogInformation(
                "Dry-run complete. No database rows were changed. Re-run with --migrate-association-contract --source-version=1 --apply to execute.");
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var backupTable = $"associations_backup_v1_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            $"CREATE TABLE social_graph.\"{backupTable}\" (LIKE social_graph.associations INCLUDING ALL);" +
            $"INSERT INTO social_graph.\"{backupTable}\" SELECT * FROM social_graph.associations;",
            cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "TRUNCATE TABLE social_graph.associations;" +
            "INSERT INTO social_graph.associations (id1, atype, id2, time) " +
            "SELECT id1, atype, id2, time FROM canonical_associations;",
            cancellationToken);

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS social_graph.graph_contract_versions (
                contract_name text PRIMARY KEY,
                version integer NOT NULL,
                applied_at timestamptz NOT NULL,
                backup_table text NOT NULL,
                statistics jsonb NOT NULL
            );
            """,
            cancellationToken);

        await using (var marker = new NpgsqlCommand(
            """
            INSERT INTO social_graph.graph_contract_versions
                (contract_name, version, applied_at, backup_table, statistics)
            VALUES (@contractName, @version, now(), @backupTable, @statistics::jsonb)
            ON CONFLICT (contract_name) DO UPDATE SET
                version = EXCLUDED.version,
                applied_at = EXCLUDED.applied_at,
                backup_table = EXCLUDED.backup_table,
                statistics = EXCLUDED.statistics;
            """,
            connection,
            transaction))
        {
            marker.Parameters.AddWithValue("contractName", ContractName);
            marker.Parameters.AddWithValue("version", CanonicalVersion);
            marker.Parameters.AddWithValue("backupTable", backupTable);
            marker.Parameters.AddWithValue(
                "statistics",
                JsonSerializer.Serialize(new { sourceRows, canonicalRows, discardedRows, resolvedConflicts }));
            await marker.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Association contract v{Version} installed. Backup retained at social_graph.{BackupTable}. Cache keys are isolated by the v2 namespace.",
            CanonicalVersion,
            backupTable);
        return 0;
    }

    public static short? MapLegacyType(short legacyType) => legacyType switch
    {
        0 => 0,
        1 => 3,
        2 => 4,
        3 => 7,
        4 => 8,
        5 => 9,
        6 => 10,
        7 => 21,
        8 => 23,
        9 => 11,
        10 => 12,
        11 => 25,
        12 => null,
        13 => 13,
        14 => 14,
        15 => 15,
        16 => 16,
        17 => 19,
        18 => 20,
        19 => 27,
        20 => 28,
        21 => 26,
        22 => 29,
        23 => 5,
        24 => 6,
        25 => 30,
        _ => null
    };

    private static int? ParseSourceVersion(IEnumerable<string> args)
    {
        var raw = args.FirstOrDefault(item => item.StartsWith(SourceVersionPrefix, StringComparison.OrdinalIgnoreCase));
        return raw is not null && int.TryParse(raw[SourceVersionPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    private static async Task BuildCanonicalProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            CREATE TEMP TABLE canonical_associations (
                id1 bigint NOT NULL,
                atype smallint NOT NULL,
                id2 bigint NOT NULL,
                time bigint NOT NULL,
                PRIMARY KEY (id1, atype, id2)
            ) ON COMMIT DROP;

            CREATE TEMP TABLE migration_conflict_counts (
                rule text PRIMARY KEY,
                affected_rows bigint NOT NULL
            ) ON COMMIT DROP;

            CREATE TEMP TABLE migration_valid_legacy_rows ON COMMIT DROP AS
            WITH mapped AS MATERIALIZED (
                SELECT
                    association.id1,
                    CASE association.atype
                        WHEN 0 THEN 0 WHEN 1 THEN 3 WHEN 2 THEN 4 WHEN 3 THEN 7
                        WHEN 4 THEN 8 WHEN 5 THEN 9 WHEN 6 THEN 10 WHEN 7 THEN 21
                        WHEN 8 THEN 23 WHEN 9 THEN 11 WHEN 10 THEN 12 WHEN 11 THEN 25
                        WHEN 13 THEN 13 WHEN 14 THEN 14 WHEN 15 THEN 15 WHEN 16 THEN 16
                        WHEN 17 THEN 19 WHEN 18 THEN 20 WHEN 19 THEN 27 WHEN 20 THEN 28
                        WHEN 21 THEN 26 WHEN 22 THEN 29 WHEN 23 THEN 5 WHEN 24 THEN 6
                        WHEN 25 THEN 30
                    END::smallint AS atype,
                    association.id2,
                    association.time,
                    source.otype AS source_type,
                    target.otype AS target_type
                FROM social_graph.associations association
                LEFT JOIN social_graph.objects source ON source.id = association.id1
                LEFT JOIN social_graph.objects target ON target.id = association.id2
                WHERE association.atype BETWEEN 0 AND 25 AND association.atype <> 12
            )
            SELECT id1, atype, id2, time
            FROM mapped
            WHERE id1 <> id2 AND source_type IS NOT NULL AND target_type IS NOT NULL
              AND (
                (atype IN (0,1,2,3,4,5,6) AND source_type = 0 AND target_type = 0) OR
                (atype = 7 AND source_type = 0 AND target_type IN (2,3,4,5,6)) OR
                (atype = 8 AND source_type IN (2,3,4,5,6) AND target_type = 0) OR
                (atype = 9 AND source_type = 0 AND target_type IN (2,3,4,5,6)) OR
                (atype = 10 AND source_type IN (2,3,4,5,6) AND target_type = 0) OR
                (atype = 11 AND source_type = 1 AND target_type = 3) OR
                (atype = 12 AND source_type = 3 AND target_type = 1) OR
                (atype IN (13,15,17) AND source_type = 0 AND target_type = 1) OR
                (atype IN (14,16,18) AND source_type = 1 AND target_type = 0) OR
                (atype = 19 AND source_type = 0 AND target_type IN (4,5)) OR
                (atype = 20 AND source_type IN (4,5) AND target_type = 0) OR
                (atype = 21 AND source_type IN (2,3,4,6) AND target_type = 6) OR
                (atype = 22 AND source_type = 6 AND target_type IN (2,3,4,6)) OR
                (atype = 23 AND source_type IN (2,5) AND target_type IN (2,4)) OR
                (atype = 24 AND source_type IN (2,4) AND target_type IN (2,5)) OR
                (atype = 25 AND source_type = 2 AND target_type = 0) OR
                (atype = 26 AND source_type IN (2,3,4,5,6) AND target_type = 0) OR
                (atype = 27 AND source_type = 0 AND target_type IN (2,3,4)) OR
                (atype = 28 AND source_type IN (2,3,4,5) AND target_type = 7) OR
                (atype = 29 AND source_type IN (0,1) AND target_type = 7) OR
                (atype = 30 AND source_type = 0 AND target_type = 1)
              );

            INSERT INTO canonical_associations (id1, atype, id2, time)
            SELECT id1, atype, id2, MAX(time)
            FROM migration_valid_legacy_rows
            GROUP BY id1, atype, id2
            ON CONFLICT (id1, atype, id2) DO UPDATE SET time = GREATEST(canonical_associations.time, EXCLUDED.time);

            WITH inverse_source AS MATERIALIZED (
                SELECT * FROM canonical_associations WHERE atype IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24)
            )
            INSERT INTO canonical_associations (id1, atype, id2, time)
            SELECT
                id2,
                CASE atype
                    WHEN 0 THEN 0 WHEN 1 THEN 2 WHEN 2 THEN 1 WHEN 3 THEN 4 WHEN 4 THEN 3
                    WHEN 5 THEN 6 WHEN 6 THEN 5 WHEN 7 THEN 8 WHEN 8 THEN 7 WHEN 9 THEN 10
                    WHEN 10 THEN 9 WHEN 11 THEN 12 WHEN 12 THEN 11 WHEN 13 THEN 14 WHEN 14 THEN 13
                    WHEN 15 THEN 16 WHEN 16 THEN 15 WHEN 17 THEN 18 WHEN 18 THEN 17 WHEN 19 THEN 20
                    WHEN 20 THEN 19 WHEN 21 THEN 22 WHEN 22 THEN 21 WHEN 23 THEN 24 WHEN 24 THEN 23
                END::smallint,
                id1,
                time
            FROM inverse_source
            ON CONFLICT (id1, atype, id2) DO UPDATE SET time = GREATEST(canonical_associations.time, EXCLUDED.time);

            INSERT INTO migration_conflict_counts (rule, affected_rows)
            SELECT 'block_over_friend_follow_request', COUNT(*)
            FROM canonical_associations lower_priority
            WHERE lower_priority.atype IN (0,1,2,3,4)
              AND EXISTS (
                SELECT 1 FROM canonical_associations block_edge
                WHERE block_edge.id1 = lower_priority.id1
                  AND block_edge.id2 = lower_priority.id2
                  AND block_edge.atype IN (5,6)
              );

            DELETE FROM canonical_associations lower_priority
            WHERE lower_priority.atype IN (0,1,2,3,4)
              AND EXISTS (
                SELECT 1 FROM canonical_associations block_edge
                WHERE block_edge.id1 = lower_priority.id1
                  AND block_edge.id2 = lower_priority.id2
                  AND block_edge.atype IN (5,6)
              );

            INSERT INTO migration_conflict_counts (rule, affected_rows)
            SELECT 'friend_over_follow_request', COUNT(*)
            FROM canonical_associations lower_priority
            WHERE lower_priority.atype IN (1,2,3,4)
              AND EXISTS (
                SELECT 1 FROM canonical_associations friend_edge
                WHERE friend_edge.id1 = lower_priority.id1
                  AND friend_edge.id2 = lower_priority.id2
                  AND friend_edge.atype = 0
              );

            DELETE FROM canonical_associations lower_priority
            WHERE lower_priority.atype IN (1,2,3,4)
              AND EXISTS (
                SELECT 1 FROM canonical_associations friend_edge
                WHERE friend_edge.id1 = lower_priority.id1
                  AND friend_edge.id2 = lower_priority.id2
                  AND friend_edge.atype = 0
              );

            INSERT INTO migration_conflict_counts (rule, affected_rows)
            SELECT 'membership_over_join_request', COUNT(*)
            FROM canonical_associations join_request
            WHERE (join_request.atype = 17 AND EXISTS (
                    SELECT 1 FROM canonical_associations membership
                    WHERE membership.id1 = join_request.id1 AND membership.id2 = join_request.id2
                      AND membership.atype IN (13,15)))
               OR (join_request.atype = 18 AND EXISTS (
                    SELECT 1 FROM canonical_associations membership
                    WHERE membership.id1 = join_request.id1 AND membership.id2 = join_request.id2
                      AND membership.atype IN (14,16)));

            DELETE FROM canonical_associations join_request
            WHERE (join_request.atype = 17 AND EXISTS (
                    SELECT 1 FROM canonical_associations membership
                    WHERE membership.id1 = join_request.id1 AND membership.id2 = join_request.id2
                      AND membership.atype IN (13,15)))
               OR (join_request.atype = 18 AND EXISTS (
                    SELECT 1 FROM canonical_associations membership
                    WHERE membership.id1 = join_request.id1 AND membership.id2 = join_request.id2
                      AND membership.atype IN (14,16)));

            INSERT INTO migration_conflict_counts (rule, affected_rows)
            SELECT 'admin_over_member', COUNT(*)
            FROM canonical_associations member_edge
            WHERE (member_edge.atype = 13 AND EXISTS (
                    SELECT 1 FROM canonical_associations admin_edge
                    WHERE admin_edge.id1 = member_edge.id1 AND admin_edge.id2 = member_edge.id2 AND admin_edge.atype = 15))
               OR (member_edge.atype = 14 AND EXISTS (
                    SELECT 1 FROM canonical_associations admin_edge
                    WHERE admin_edge.id1 = member_edge.id1 AND admin_edge.id2 = member_edge.id2 AND admin_edge.atype = 16));

            DELETE FROM canonical_associations member_edge
            WHERE (member_edge.atype = 13 AND EXISTS (
                    SELECT 1 FROM canonical_associations admin_edge
                    WHERE admin_edge.id1 = member_edge.id1 AND admin_edge.id2 = member_edge.id2 AND admin_edge.atype = 15))
               OR (member_edge.atype = 14 AND EXISTS (
                    SELECT 1 FROM canonical_associations admin_edge
                    WHERE admin_edge.id1 = member_edge.id1 AND admin_edge.id2 = member_edge.id2 AND admin_edge.atype = 16));
            """,
            cancellationToken);
    }

    private const string DiscardedRowsSql = """
        SELECT
            (SELECT COUNT(*) FROM social_graph.associations) -
            (SELECT COUNT(*) FROM migration_valid_legacy_rows);
        """;

    private static async Task<int?> GetInstalledVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "social_graph.graph_contract_versions", cancellationToken))
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            "SELECT version FROM social_graph.graph_contract_versions WHERE contract_name = @contractName;",
            connection,
            transaction);
        command.Parameters.AddWithValue("contractName", ContractName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@tableName) IS NOT NULL;", connection, transaction);
        command.Parameters.AddWithValue("tableName", tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadConflictCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT rule, affected_rows FROM migration_conflict_counts ORDER BY rule;",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
