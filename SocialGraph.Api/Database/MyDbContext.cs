namespace SocialGraph.Api.Database;
using Microsoft.EntityFrameworkCore;

public class MyDbContext : DbContext
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) { }

    public DbSet<Objects> ObjectsTb { get; set; }
    public DbSet<Associations> AssociationsTb { get; set; }
    public DbSet<IntegrationOutboxMessage> IntegrationOutboxTb { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("social_graph");

        modelBuilder.Entity<Objects>(entity =>
        {
            entity.ToTable("objects");
            entity.HasKey(e => e.id);
            entity.Property(e => e.id).ValueGeneratedNever();
            entity.Property(e => e.otype).IsRequired();
            entity.Property(e => e.data).HasColumnType("jsonb");
            // Reel candidate selection and the story cleanup sweep filter on otype.
            entity.HasIndex(e => new { e.otype, e.id }).HasDatabaseName("idx_objects_type_id");
        });

        modelBuilder.Entity<Associations>(entity =>
        {
            entity.ToTable("associations");
            entity.HasKey(e => new { e.id1, e.atype, e.id2 });
            // No index on (id1, atype, id2): that is the primary key, and declaring it
            // again produced a second B-tree maintained on every write for no benefit.
            entity.HasIndex(e => new { e.id2, e.atype, e.id1 }).HasDatabaseName("idx_associations_inverse");
            // Paged reads order by time within a bucket; see migrations/20260727_add_hot_path_indexes.sql.
            entity.HasIndex(e => new { e.id1, e.atype, e.time, e.id2 }).HasDatabaseName("idx_associations_time");
            entity.Property(e => e.time).IsRequired();
            entity.Property(e => e.requested_at)
                .HasColumnType("timestamp with time zone")
                .HasComputedColumnSql(
                    """
                    CASE
                        WHEN atype IN (17, 18)
                        THEN to_timestamp("time"::double precision / 1000.0)
                        ELSE NULL
                    END
                    """,
                    stored: true);
        });

        modelBuilder.Entity<IntegrationOutboxMessage>(entity =>
        {
            entity.ToTable("integration_outbox");
            entity.HasKey(item => item.id);
            entity.HasIndex(item => item.idempotency_key)
                .IsUnique()
                .HasDatabaseName("ux_integration_outbox_idempotency_key");
            entity.HasIndex(item => new { item.status, item.available_at })
                .HasDatabaseName("ix_integration_outbox_dispatch");
            entity.Property(item => item.id).ValueGeneratedNever();
            entity.Property(item => item.event_type).HasMaxLength(100).IsRequired();
            entity.Property(item => item.idempotency_key).HasMaxLength(200).IsRequired();
            entity.Property(item => item.payload).HasColumnType("jsonb").IsRequired();
            entity.Property(item => item.created_at).IsRequired();
            entity.Property(item => item.available_at).IsRequired();
            entity.Property(item => item.max_attempts).IsRequired();
            entity.Property(item => item.status).IsRequired();
            entity.Property(item => item.locked_by).HasMaxLength(200);
            entity.Property(item => item.last_error).HasMaxLength(2000);
        });
    }
}
