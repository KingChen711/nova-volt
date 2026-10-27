using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Nvm.Foundation.Runtime;

/// <summary>DbContext dựng model từ <see cref="ModelRegistry"/>. Chỉ Foundation thấy nó; handler chỉ thấy <see cref="IPlatform"/>.</summary>
internal sealed class FoundationDbContext : DbContext
{
    private readonly ModelRegistry _registry;
    private readonly TimeProvider _time;

    private FoundationDbContext(DbContextOptions<FoundationDbContext> options, ModelRegistry registry, TimeProvider time)
        : base(options)
    {
        _registry = registry;
        _time = time;
    }

    public ModelRegistry Registry => _registry;

    public static FoundationDbContext Create(DbConnection connection, ModelRegistry registry, TimeProvider time, bool tracking)
    {
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseSqlServer(connection)
            .UseQueryTrackingBehavior(tracking ? QueryTrackingBehavior.TrackAll : QueryTrackingBehavior.NoTracking)
            .ReplaceService<IModelCacheKeyFactory, RegistryModelCacheKeyFactory>()
            .Options;
        return new FoundationDbContext(options, registry, time);
    }

    /// <summary>Đóng dấu thời gian và ép <see cref="MandatoryAttribute"/> trước khi ghi: ràng buộc ở tầng model, không ở cột DB.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var now = _time.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<ModelObject>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            Validate(entry.Entity);
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedOn = now;
            }

            entry.Entity.LastUpdatedOn = now;
        }

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entity in _registry.Entities)
        {
            var b = modelBuilder.Entity(entity.Type);
            b.ToTable(entity.TableName, "dbo");
            MapSystemColumns(b, entity.Type);
            b.Property(nameof(Entity.NId)).HasMaxLength(255).IsRequired();
            foreach (var p in entity.Properties)
            {
                MapProperty(b.Property(p.Name), p);
            }
        }

        foreach (var table in _registry.FacetTables)
        {
            // Mọi facet của một entity chung một bảng: TPH với gốc Facet<TEntity>, cột phân biệt FacetType.
            var root = modelBuilder.Entity(table.RootType);
            root.ToTable(table.TableName, "dbo");
            MapSystemColumns(root, table.RootType);
            root.Property(nameof(FacetBase.EntityId)).HasColumnName(table.EntityIdColumn);
            var discriminator = root.HasDiscriminator("FacetType", typeof(string));
            root.Property("FacetType").HasMaxLength(128);
            foreach (var facet in table.Facets)
            {
                var fb = modelBuilder.Entity(facet.Type).HasBaseType(table.RootType);
                discriminator.HasValue(facet.Type, facet.Name);
                foreach (var p in facet.Properties)
                {
                    MapProperty(fb.Property(p.Name), p);
                }
            }
        }
    }

    private void Validate(ModelObject item)
    {
        var properties = _registry.EntityOf(item.GetType())?.Properties ?? _registry.FacetOf(item.GetType())?.Properties ?? [];
        foreach (var p in properties.Where(p => p.Mandatory))
        {
            if (p.Property.GetValue(item) is null or "")
            {
                throw new ModelValidationException($"{item.GetType().Name}.{p.Name} is mandatory.");
            }
        }

        if (item is Entity { NId: null or "" })
        {
            throw new ModelValidationException($"{item.GetType().Name}.NId is mandatory.");
        }
    }

    private static void MapSystemColumns(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder b, Type type)
    {
        b.HasKey(nameof(ModelObject.Id));
        b.Property(nameof(ModelObject.Id)).ValueGeneratedNever();
        b.Property(nameof(ModelObject.CreatedOn));
        b.Property(nameof(ModelObject.LastUpdatedOn));
        b.Property(nameof(ModelObject.IsDeleted));
        b.Property<byte[]>("Version").HasColumnName("OptimisticVersion").IsRowVersion();
        b.Ignore(nameof(ModelObject.OptimisticVersion));

        // Đọc qua platform tự bỏ bản đã xóa; SQL trực tiếp thì không (lab L03).
        var item = Expression.Parameter(type, "item");
        b.HasQueryFilter(Expression.Lambda(
            Expression.Equal(Expression.Property(item, nameof(ModelObject.IsDeleted)), Expression.Constant(0)), item));
    }

    private static void MapProperty(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder property, PropertyModel model)
    {
        property.HasColumnName(model.ColumnName);
        var clr = Nullable.GetUnderlyingType(model.ClrType) ?? model.ClrType;
        if (clr == typeof(string))
        {
            if (model.TextLength > 0)
            {
                property.HasMaxLength(model.TextLength);
            }
        }
        else if (clr == typeof(decimal))
        {
            property.HasPrecision(18, 6);
        }
    }

    /// <summary>Mỗi registry một model EF: test dựng nhiều registry trong một process.</summary>
    private sealed class RegistryModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((FoundationDbContext)context)._registry, designTime);
    }
}
