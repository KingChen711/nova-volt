using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Nvm.PublicObjectModel;

public sealed class PomReadDbContext(
    DbContextOptions<PomReadDbContext> options,
    IHttpContextAccessor httpContextAccessor) : DbContext(options)
{
    public DbSet<Equipment> Equipment => Set<Equipment>();

    public DbSet<ProductionUnit> ProductionUnits => Set<ProductionUnit>();

    public DbSet<WipBoardRow> WipBoard => Set<WipBoardRow>();

    // Predicate thuộc instance context, EF parameterize theo request; không giữ site trong model cache.
    public string CurrentSiteId => httpContextAccessor.HttpContext?.User.FindFirst("site_id")?.Value ?? string.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        var equipment = modelBuilder.Entity<Equipment>();
        equipment.ToTable("equipment", "pom");
        equipment.HasKey(row => row.Id);
        equipment.Property(row => row.Id).HasColumnName("id");
        equipment.Property(row => row.SiteId).HasColumnName("site_id");
        equipment.Property(row => row.EquipmentPath).HasColumnName("equipment_path");
        equipment.Property(row => row.Name).HasColumnName("name");
        equipment.Property(row => row.Line).HasColumnName("line");
        equipment.Property(row => row.Resource).HasColumnName("resource");
        equipment.Property(row => row.Revision).HasColumnName("revision");
        equipment.HasQueryFilter(row => row.SiteId == CurrentSiteId);

        var unit = modelBuilder.Entity<ProductionUnit>();
        unit.ToView("production_units_read", "pom");
        unit.HasKey(row => row.Id);
        unit.Property(row => row.Id).HasColumnName("id");
        unit.Property(row => row.SiteId).HasColumnName("site_id");
        unit.Property(row => row.SerialNumber).HasColumnName("serial_number");
        unit.Property(row => row.UnitKind).HasColumnName("unit_kind");
        unit.Property(row => row.Line).HasColumnName("line");
        unit.Property(row => row.Resource).HasColumnName("resource");
        unit.Property(row => row.EquipmentPath).HasColumnName("equipment_path");
        unit.Property(row => row.WorkOrderId).HasColumnName("work_order_id");
        unit.Property(row => row.OperationRunId).HasColumnName("operation_run_id");
        unit.Property(row => row.StepCode).HasColumnName("step_code");
        unit.Property(row => row.ExecutionState).HasColumnName("execution_state");
        unit.Property(row => row.QualityState).HasColumnName("quality_state");
        unit.Property(row => row.LocationState).HasColumnName("location_state");
        unit.Property(row => row.BlockingReasonCode).HasColumnName("blocking_reason_code");
        unit.Property(row => row.BlockingReasonText).HasColumnName("blocking_reason_text");
        unit.Property(row => row.Revision).HasColumnName("revision");
        unit.HasQueryFilter(row => row.SiteId == CurrentSiteId);

        var wip = modelBuilder.Entity<WipBoardRow>();
        wip.ToView("wip_board_read", "pom");
        wip.HasKey(row => row.Id);
        wip.Property(row => row.Id).HasColumnName("id");
        wip.Property(row => row.SiteId).HasColumnName("site_id");
        wip.Property(row => row.Line).HasColumnName("line");
        wip.Property(row => row.StepCode).HasColumnName("step_code");
        wip.Property(row => row.QualityState).HasColumnName("quality_state");
        wip.Property(row => row.UnitCount).HasColumnName("unit_count");
        wip.Property(row => row.Revision).HasColumnName("revision");
        wip.HasQueryFilter(row => row.SiteId == CurrentSiteId);
    }
}
