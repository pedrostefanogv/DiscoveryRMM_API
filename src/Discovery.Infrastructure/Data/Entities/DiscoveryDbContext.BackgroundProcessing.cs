using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// Estado dos ciclos agendados por escopo (métricas de atendente e triagem por IA).
public partial class DiscoveryDbContext
{
    static partial void ConfigureBackgroundProcessing(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessingScopeState>(entity =>
        {
            entity.ToTable("processing_scope_state");
            entity.HasKey(state => state.Id);
            entity.HasIndex(state => new { state.ScopeType, state.ScopeId })
                .IsUnique()
                .HasDatabaseName("ux_processing_scope_state_type_scope");
            entity.HasIndex(state => state.LastRunAt)
                .HasDatabaseName("ix_processing_scope_state_last_run");

            entity.Property(state => state.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(state => state.ScopeType).HasColumnName("scope_type").HasMaxLength(40);
            entity.Property(state => state.ScopeId).HasColumnName("scope_id");
            entity.Property(state => state.LastRunAt).HasColumnName("last_run_at").HasColumnType("timestamptz");
            entity.Property(state => state.LastResultJson).HasColumnName("last_result_json").HasColumnType("jsonb");
            entity.Property(state => state.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });
    }
}
