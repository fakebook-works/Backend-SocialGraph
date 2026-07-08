namespace SocialGraph.Api.Database;
using Microsoft.EntityFrameworkCore;

public class MyDbContext : DbContext
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) {}
    
    public DbSet<Objects> ObjectsTb { get; set; }
    public DbSet<Associations> AssociationsTb { get; set; }
    
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
    }
}
