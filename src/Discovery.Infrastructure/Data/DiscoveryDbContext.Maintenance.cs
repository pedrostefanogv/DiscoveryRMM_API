using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Mapeamento do histórico da manutenção física do banco
/// (<c>database_maintenance_runs</c>).
/// </summary>
public partial class DiscoveryDbContext
{
    static partial void ConfigureMaintenanceRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DatabaseMaintenanceRun>(entity =>
        {
            entity.ToTable("database_maintenance_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.OperationsJson).HasColumnType("jsonb").HasDefaultValue("[]");
            entity.Property(run => run.ErrorMessage).HasMaxLength(2000);
            entity.HasIndex(run => run.StartedAtUtc)
                .HasDatabaseName("ix_database_maintenance_runs_started_at");
        });
    }
}
