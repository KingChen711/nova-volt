using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Nvm.PublicObjectModel;

public sealed class PomReadDbContext(
    DbContextOptions<PomReadDbContext> options,
    IHttpContextAccessor httpContextAccessor) : DbContext(options)
{
    public DbSet<Equipment> Equipment => Set<Equipment>();

    // Predicate thuộc instance context, EF parameterize theo request; không giữ site trong model cache.
    public string CurrentSiteId => httpContextAccessor.HttpContext?.User.FindFirst("site_id")?.Value ?? string.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        var entity = modelBuilder.Entity<Equipment>();
        entity.ToTable("equipment", "pom");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id");
        entity.Property(row => row.SiteId).HasColumnName("site_id");
        entity.Property(row => row.EquipmentPath).HasColumnName("equipment_path");
        entity.Property(row => row.Name).HasColumnName("name");
        entity.Property(row => row.Line).HasColumnName("line");
        entity.Property(row => row.Resource).HasColumnName("resource");
        entity.Property(row => row.Revision).HasColumnName("revision");
        entity.HasQueryFilter(row => row.SiteId == CurrentSiteId);
    }
}
