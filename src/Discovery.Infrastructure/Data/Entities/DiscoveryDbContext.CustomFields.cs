using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── CustomFields: CustomFieldDefinition, CustomFieldValue, CustomFieldExecutionAccess ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureCustomFields(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomFieldDefinition>(entity =>
        {
            entity.ToTable("custom_field_definitions");
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => new { d.ScopeType, d.Name }).IsUnique().HasDatabaseName("ux_custom_field_definitions_scope_name");
            entity.HasIndex(d => new { d.ScopeType, d.IsActive }).HasDatabaseName("ix_custom_field_definitions_scope_active");

            entity.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(d => d.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(d => d.Label).HasColumnName("label").HasMaxLength(200);
            entity.Property(d => d.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(d => d.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(d => d.DataType).HasColumnName("data_type").HasConversion<int>();
            entity.Property(d => d.IsRequired).HasColumnName("is_required");
            entity.Property(d => d.IsActive).HasColumnName("is_active");
            entity.Property(d => d.IsSecret).HasColumnName("is_secret");
            entity.Property(d => d.OptionsJson).HasColumnName("options_json").HasColumnType("jsonb");
            entity.Property(d => d.ValidationRegex).HasColumnName("validation_regex").HasMaxLength(500);
            entity.Property(d => d.MinLength).HasColumnName("min_length");
            entity.Property(d => d.MaxLength).HasColumnName("max_length");
            entity.Property(d => d.MinValue).HasColumnName("min_value").HasColumnType("numeric(18,6)");
            entity.Property(d => d.MaxValue).HasColumnName("max_value").HasColumnType("numeric(18,6)");
            entity.Property(d => d.AllowRuntimeRead).HasColumnName("allow_runtime_read");
            entity.Property(d => d.AllowAgentWrite).HasColumnName("allow_agent_write");
            entity.Property(d => d.RuntimeAccessMode).HasColumnName("runtime_access_mode").HasConversion<int>();
            entity.Property(d => d.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(d => d.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomFieldValue>(entity =>
        {
            entity.ToTable("custom_field_values");
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => new { v.DefinitionId, v.EntityKey }).IsUnique().HasDatabaseName("ux_custom_field_values_definition_entity");
            entity.HasIndex(v => new { v.ScopeType, v.EntityId }).HasDatabaseName("ix_custom_field_values_scope_entity");

            entity.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(v => v.DefinitionId).HasColumnName("definition_id");
            entity.Property(v => v.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(v => v.EntityId).HasColumnName("entity_id");
            entity.Property(v => v.EntityKey).HasColumnName("entity_key").HasMaxLength(64);
            entity.Property(v => v.ValueJson).HasColumnName("value_json").HasColumnType("jsonb");
            entity.Property(v => v.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            entity.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(v => v.DefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomFieldExecutionAccess>(entity =>
        {
            entity.ToTable("custom_field_execution_access");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.DefinitionId, a.TaskId, a.ScriptId }).IsUnique().HasDatabaseName("ux_custom_field_execution_access_def_task_script");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.DefinitionId).HasColumnName("definition_id");
            entity.Property(a => a.TaskId).HasColumnName("task_id");
            entity.Property(a => a.ScriptId).HasColumnName("script_id");
            entity.Property(a => a.CanRead).HasColumnName("can_read");
            entity.Property(a => a.CanWrite).HasColumnName("can_write");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(a => a.DefinitionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AutomationTaskDefinition>().WithMany().HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(a => a.ScriptId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
