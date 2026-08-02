namespace SocialGraph.Api.Tests;

using SocialGraph.Api.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public void Automatic_migrations_are_enabled_by_default()
    {
        var options = new DatabaseMigrationOptions();

        Assert.True(options.Enabled);
        Assert.InRange(options.CommandTimeoutSeconds, 1, 3_600);
    }

    [Fact]
    public async Task Disabled_migrations_do_not_require_a_database_connection()
    {
        var migrator = new SocialGraphDatabaseMigrator(
            new ConfigurationBuilder().Build(),
            Options.Create(new DatabaseMigrationOptions { Enabled = false }),
            NullLogger<SocialGraphDatabaseMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);
    }

    [Fact]
    public void Embedded_migrations_are_registered_in_version_order()
    {
        Assert.Equal(
            new[]
            {
                "00000000_schema",
                "20260727_add_hot_path_indexes",
                "20260802_create_integration_outbox"
            },
            SocialGraphDatabaseMigrator.MigrationVersions);

        var assembly = typeof(SocialGraphDatabaseMigrator).Assembly;
        foreach (var resourceName in SocialGraphDatabaseMigrator.MigrationResourceNames)
        {
            using var resource = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(resource);
        }
    }

    [Fact]
    public void Migration_checksum_is_deterministic_and_detects_drift()
    {
        var checksum = SocialGraphDatabaseMigrator.ComputeChecksum("SELECT 1;");

        Assert.Equal(64, checksum.Length);
        Assert.Equal(checksum, SocialGraphDatabaseMigrator.ComputeChecksum("SELECT 1;"));
        Assert.Equal(
            SocialGraphDatabaseMigrator.ComputeChecksum("SELECT 1;\nSELECT 2;\n"),
            SocialGraphDatabaseMigrator.ComputeChecksum("SELECT 1;\r\nSELECT 2;\r\n"));
        Assert.NotEqual(checksum, SocialGraphDatabaseMigrator.ComputeChecksum("SELECT 2;"));
        Assert.True(SocialGraphDatabaseMigrator.ChecksumsMatch($"{checksum}  ", checksum));
    }
}
