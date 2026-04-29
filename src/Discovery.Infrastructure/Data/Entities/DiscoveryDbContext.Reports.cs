using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Reports: ReportTemplate, ReportExecution, ReportTemplateHistory, ReportSchedule ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureReports(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("report_templates");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.ClientId, t.DatasetType, t.IsActive }).HasDatabaseName("ix_report_templates_client_dataset_active");
            entity.HasIndex(t => t.CreatedAt).HasDatabaseName("ix_report_templates_created_at");

            entity.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(t => t.ClientId).HasColumnName("client_id");
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(t => t.Description).HasColumnName("description").HasMaxLength(2000);
            entity.Property(t => t.Instructions).HasColumnName("instructions").HasMaxLength(4000);
            entity.Property(t => t.ExecutionSchemaJson).HasColumnName("execution_schema_json").HasColumnType("jsonb");
            entity.Property(t => t.DatasetType).HasColumnName("dataset_type").HasConversion<int>();
            entity.Property(t => t.DefaultFormat).HasColumnName("default_format").HasConversion<int>();
            entity.Property(t => t.LayoutJson).HasColumnName("layout_json").HasColumnType("jsonb");
            entity.Property(t => t.FiltersJson).HasColumnName("filters_json").HasColumnType("jsonb");
            entity.Property(t => t.IsActive).HasColumnName("is_active");
            entity.Property(t => t.IsBuiltIn).HasColumnName("is_built_in");
            entity.Property(t => t.Version).HasColumnName("version");
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(t => t.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(t => t.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);

            entity.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportExecution>(entity =>
        {
            entity.ToTable("report_executions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ClientId, e.Status, e.CreatedAt }).HasDatabaseName("ix_report_executions_client_status_created");
            entity.HasIndex(e => e.TemplateId).HasDatabaseName("ix_report_executions_template");

            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.TemplateId).HasColumnName("template_id");
            entity.Property(e => e.ClientId).HasColumnName("client_id");
            entity.Property(e => e.Format).HasColumnName("format").HasConversion<int>();
            entity.Property(e => e.FiltersJson).HasColumnName("filters_json").HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(e => e.StorageProviderType).HasColumnName("storage_provider_type");
            entity.Property(e => e.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(200);
            entity.Property(e => e.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1000);
            entity.Property(e => e.StorageContentType).HasColumnName("storage_content_type").HasMaxLength(200);
            entity.Property(e => e.StorageSizeBytes).HasColumnName("storage_size_bytes");
            entity.Property(e => e.StorageChecksum).HasColumnName("storage_checksum").HasMaxLength(200);
            entity.Property(e => e.StoragePresignedUrl).HasColumnName("storage_presigned_url").HasMaxLength(2000);
            entity.Property(e => e.StoragePresignedUrlGeneratedAt).HasColumnName("storage_presigned_url_generated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RowCount).HasColumnName("row_count");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
            entity.Property(e => e.ExecutionTimeMs).HasColumnName("execution_time_ms");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");

            entity.HasOne<ReportTemplate>().WithMany().HasForeignKey(e => e.TemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ReportSchedule>().WithMany().HasForeignKey(e => e.ScheduleId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReportTemplateHistory>(entity =>
        {
            entity.ToTable("report_template_history");
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => new { h.TemplateId, h.Version }).HasDatabaseName("ix_report_template_history_template_version");
            entity.HasIndex(h => h.ChangedAt).HasDatabaseName("ix_report_template_history_changed_at");

            entity.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(h => h.TemplateId).HasColumnName("template_id");
            entity.Property(h => h.Version).HasColumnName("version");
            entity.Property(h => h.ChangeType).HasColumnName("change_type").HasMaxLength(32);
            entity.Property(h => h.SnapshotJson).HasColumnName("snapshot_json").HasColumnType("jsonb");
            entity.Property(h => h.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");
            entity.Property(h => h.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
        });

        modelBuilder.Entity<ReportSchedule>(entity =>
        {
            entity.ToTable("report_schedules");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => s.TemplateId).HasDatabaseName("ix_report_schedules_template_id");
            entity.HasIndex(s => s.ClientId).HasDatabaseName("ix_report_schedules_client_id");
            entity.HasIndex(s => s.NextTriggerAt).HasDatabaseName("ix_report_schedules_next_trigger_at");
            entity.HasIndex(s => s.IsActive).HasDatabaseName("ix_report_schedules_is_active");

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.TemplateId).HasColumnName("template_id");
            entity.Property(s => s.ClientId).HasColumnName("client_id");
            entity.Property(s => s.Format).HasColumnName("format").HasConversion<int>();
            entity.Property(s => s.FiltersJson).HasColumnName("filters_json").HasColumnType("jsonb");
            entity.Property(s => s.ScheduleLabel).HasColumnName("schedule_label").HasMaxLength(300);
            entity.Property(s => s.CronExpression).HasColumnName("cron_expression").HasMaxLength(200);
            entity.Property(s => s.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(100);
            entity.Property(s => s.MaxRetainedExecutions).HasColumnName("max_retained_executions");
            entity.Property(s => s.IsActive).HasColumnName("is_active");
            entity.Property(s => s.NextTriggerAt).HasColumnName("next_trigger_at").HasColumnType("timestamptz");
            entity.Property(s => s.LastTriggeredAt).HasColumnName("last_triggered_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(s => s.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);

            entity.HasOne<ReportTemplate>().WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
