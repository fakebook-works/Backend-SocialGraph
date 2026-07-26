namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Database;

/// <summary>
/// Guards the index set on the two hottest tables. Neither the primary key nor
/// idx_associations_inverse contains the time column, yet nearly every read orders by it,
/// so a bucket read had to sort the whole bucket to return one page. idx_associations was
/// meanwhile defined on exactly the primary key columns in the same order — a second
/// B-tree maintained on every write that could never be chosen over the key itself.
/// </summary>
public sealed class SchemaIndexTests
{
    [Fact]
    public void Associations_are_indexed_for_time_ordered_paging()
    {
        var index = Assert.Single(
            IndexNames<Associations>(),
            name => name == "idx_associations_time");

        Assert.Equal("idx_associations_time", index);
    }

    [Fact]
    public void Associations_do_not_carry_an_index_duplicating_the_primary_key()
    {
        Assert.DoesNotContain("idx_associations", IndexNames<Associations>());
    }

    [Fact]
    public void Associations_keep_the_reverse_traversal_index()
    {
        Assert.Contains("idx_associations_inverse", IndexNames<Associations>());
    }

    [Fact]
    public void Objects_are_indexed_by_type()
    {
        Assert.Contains("idx_objects_type_id", IndexNames<Objects>());
    }

    [Fact]
    public void No_declared_index_repeats_the_primary_key_of_its_table()
    {
        using var context = CreateContext();
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey()?.Properties.Select(property => property.Name).ToArray();
            if (key is null) continue;

            foreach (var index in entity.GetIndexes())
            {
                var columns = index.Properties.Select(property => property.Name).ToArray();
                Assert.False(
                    columns.SequenceEqual(key),
                    $"{entity.ClrType.Name}.{index.GetDatabaseName()} repeats the primary key exactly.");
            }
        }
    }

    private static IReadOnlyList<string> IndexNames<TEntity>() where TEntity : class
    {
        using var context = CreateContext();
        return context.Model
            .FindEntityType(typeof(TEntity))!
            .GetIndexes()
            .Select(index => index.GetDatabaseName()!)
            .ToArray();
    }

    private static MyDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
