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
        });

        modelBuilder.Entity<Associations>(entity =>
        {
            entity.ToTable("associations");
            entity.HasKey(e => new { e.id1, e.atype, e.id2 });
            entity.HasIndex(e => new { e.id1, e.atype, e.id2 }).HasDatabaseName("idx_associations");
            entity.HasIndex(e => new { e.id2, e.atype, e.id1 }).HasDatabaseName("idx_associations_inverse");
            entity.Property(e => e.time).IsRequired();
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
