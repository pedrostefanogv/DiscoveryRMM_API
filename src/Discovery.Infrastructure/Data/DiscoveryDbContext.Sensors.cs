using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Sensores de monitoramento: definições, estado, amostras (série bruta),
//    agregados horários (rollup) e timeline de eventos ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureSensors(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SensorDefinition>(entity =>
        {
            entity.ToTable("sensor_definitions");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.ScopeType, s.ClientId, s.SiteId, s.AgentId })
                .HasDatabaseName("ix_sensor_definitions_scope");
            entity.HasIndex(s => new { s.IsActive, s.UpdatedAt })
                .HasDatabaseName("ix_sensor_definitions_active_updated");
            entity.HasIndex(s => s.Key).HasDatabaseName("ix_sensor_definitions_key");
            // Unicidade por escopo e apenas entre ativas é um índice parcial
            // expressional (não representável no modelo EF) criado na M259:
            //   UNIQUE (coalesce(client_id, site_id, agent_id), key)
            //   WHERE key IS NOT NULL AND deleted_at IS NULL

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.Key).HasColumnName("key").HasMaxLength(100);
            entity.Property(s => s.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(s => s.Description).HasColumnName("description").HasMaxLength(2000);
            entity.Property(s => s.SensorType).HasColumnName("sensor_type").HasConversion<int>();
            entity.Property(s => s.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(s => s.ClientId).HasColumnName("client_id");
            entity.Property(s => s.SiteId).HasColumnName("site_id");
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.IncludeTagsJson).HasColumnName("include_tags_json").HasColumnType("jsonb");
            entity.Property(s => s.ExcludeTagsJson).HasColumnName("exclude_tags_json").HasColumnType("jsonb");
            entity.Property(s => s.Target).HasColumnName("target").HasMaxLength(300);
            entity.Property(s => s.WarningThreshold).HasColumnName("warning_threshold").HasColumnType("numeric(18,4)");
            entity.Property(s => s.CriticalThreshold).HasColumnName("critical_threshold").HasColumnType("numeric(18,4)");
            entity.Property(s => s.ThresholdUnit).HasColumnName("threshold_unit").HasConversion<int>();
            entity.Property(s => s.Comparison).HasColumnName("comparison").HasConversion<int>();
            entity.Property(s => s.OptionsJson).HasColumnName("options_json").HasColumnType("jsonb");
            entity.Property(s => s.ScriptId).HasColumnName("script_id");
            entity.Property(s => s.ChannelsJson).HasColumnName("channels_json").HasColumnType("jsonb");
            entity.Property(s => s.ExtractorsJson).HasColumnName("extractors_json").HasColumnType("jsonb");
            entity.Property(s => s.ExitCodeMapJson).HasColumnName("exit_code_map_json").HasColumnType("jsonb");
            entity.Property(s => s.ChartJson).HasColumnName("chart_json").HasColumnType("jsonb");
            entity.Property(s => s.IntervalSeconds).HasColumnName("interval_seconds").HasDefaultValue(0);
            entity.Property(s => s.TimeoutSeconds).HasColumnName("timeout_seconds").HasDefaultValue(60);
            entity.Property(s => s.StoreSamples).HasColumnName("store_samples").HasDefaultValue(true);
            entity.Property(s => s.FailsBeforeAlert).HasColumnName("fails_before_alert").HasDefaultValue(2);
            // Histerese de escalação (M266).
            entity.Property(s => s.SustainSamples).HasColumnName("sustain_samples").HasDefaultValue(1);
            entity.Property(s => s.MinSeverityToAlert).HasColumnName("min_severity_to_alert").HasConversion<int>();
            entity.Property(s => s.SeverityWhenFailing).HasColumnName("severity_when_failing").HasConversion<int>();
            entity.Property(s => s.AlertOnSensorError).HasColumnName("alert_on_sensor_error").HasDefaultValue(true);
            // Vínculo do sensor com o auto-ticket (M268).
            entity.Property(s => s.AutoTicketPolicy).HasColumnName("auto_ticket_policy").HasConversion<int>().HasDefaultValue(SensorAutoTicketPolicy.OnEscalation);
            entity.Property(s => s.AutoTicketMinDurationSeconds).HasColumnName("auto_ticket_min_duration_seconds").HasDefaultValue(0);
            entity.Property(s => s.AutoTicketImpact).HasColumnName("auto_ticket_impact").HasConversion<int>().HasDefaultValue(AutoTicketImpact.Medium);
            entity.Property(s => s.DisplayOrder).HasColumnName("display_order").HasDefaultValue(50);
            entity.Property(s => s.IsPinned).HasColumnName("is_pinned").HasDefaultValue(false);
            entity.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(s => s.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            entity.Property(s => s.LastUpdatedAt).HasColumnName("last_updated_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(256);

            entity.HasOne<Client>().WithMany().HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Site>().WithMany().HasForeignKey(s => s.SiteId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Agent>().WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(s => s.ScriptId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentSensorState>(entity =>
        {
            entity.ToTable("agent_sensor_states");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.AgentId, s.SensorId, s.TargetKey })
                .HasDatabaseName("ux_agent_sensor_states_agent_sensor_target").IsUnique();
            entity.HasIndex(s => new { s.AgentId, s.Status })
                .HasDatabaseName("ix_agent_sensor_states_agent_status");

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.SensorId).HasColumnName("sensor_id");
            entity.Property(s => s.TargetKey).HasColumnName("target_key").HasMaxLength(100).HasDefaultValue(string.Empty);
            // Enum com conversão para int NÃO aceita HasDefaultValue(int) no EF
            // (o default do banco está na migration).
            entity.Property(s => s.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(s => s.Severity).HasColumnName("severity").HasConversion<int?>();
            entity.Property(s => s.Message).HasColumnName("message").HasMaxLength(1000);
            entity.Property(s => s.ChannelsJson).HasColumnName("channels_json").HasColumnType("jsonb");
            entity.Property(s => s.PrimaryChannel).HasColumnName("primary_channel").HasMaxLength(100);
            entity.Property(s => s.PrimaryValue).HasColumnName("primary_value").HasColumnType("numeric(18,4)");
            entity.Property(s => s.ExitCode).HasColumnName("exit_code");
            entity.Property(s => s.DurationMs).HasColumnName("duration_ms");
            entity.Property(s => s.OutputExcerpt).HasColumnName("output_excerpt").HasColumnType("text");
            entity.Property(s => s.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);
            entity.Property(s => s.TotalRuns).HasColumnName("total_runs").HasDefaultValue(0L);
            entity.Property(s => s.TotalFailures).HasColumnName("total_failures").HasDefaultValue(0L);
            entity.Property(s => s.LastRunAt).HasColumnName("last_run_at").HasColumnType("timestamptz");
            entity.Property(s => s.LastStatusChangedAt).HasColumnName("last_status_changed_at").HasColumnType("timestamptz");
            entity.Property(s => s.LastAlertedAt).HasColumnName("last_alerted_at").HasColumnType("timestamptz");
            entity.Property(s => s.LastAlertSeverity).HasColumnName("last_alert_severity").HasConversion<int?>();
            entity.Property(s => s.SparklineJson).HasColumnName("sparkline_json").HasColumnType("jsonb");
            entity.Property(s => s.LastRunId).HasColumnName("last_run_id");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SensorDefinition>().WithMany().HasForeignKey(s => s.SensorId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSensorSample>(entity =>
        {
            entity.ToTable("agent_sensor_samples");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.HasIndex(s => new { s.AgentId, s.SensorId, s.TargetKey, s.CapturedAt })
                .HasDatabaseName("ix_sensor_samples_agent_sensor_target_captured");
            entity.HasIndex(s => new { s.AgentId, s.SensorId, s.TargetKey, s.RunId })
                .HasDatabaseName("ux_sensor_samples_run").IsUnique();

            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.SensorId).HasColumnName("sensor_id");
            entity.Property(s => s.TargetKey).HasColumnName("target_key").HasMaxLength(100).HasDefaultValue(string.Empty);
            entity.Property(s => s.CollectedAt).HasColumnName("collected_at").HasColumnType("timestamptz");
            entity.Property(s => s.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamptz");
            entity.Property(s => s.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(s => s.Severity).HasColumnName("severity").HasConversion<int?>();
            entity.Property(s => s.ChannelsJson).HasColumnName("channels_json").HasColumnType("jsonb");
            entity.Property(s => s.Message).HasColumnName("message").HasMaxLength(500);
            entity.Property(s => s.RunId).HasColumnName("run_id");
            entity.Property(s => s.ExitCode).HasColumnName("exit_code");

            entity.HasOne<Agent>().WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SensorDefinition>().WithMany().HasForeignKey(s => s.SensorId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSensorRollup>(entity =>
        {
            entity.ToTable("agent_sensor_rollups");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            entity.HasIndex(r => new { r.AgentId, r.SensorId, r.TargetKey, r.Channel, r.BucketStart })
                .HasDatabaseName("ux_sensor_rollups_bucket").IsUnique();
            entity.HasIndex(r => r.BucketStart).HasDatabaseName("ix_sensor_rollups_bucket_start");

            entity.Property(r => r.AgentId).HasColumnName("agent_id");
            entity.Property(r => r.SensorId).HasColumnName("sensor_id");
            entity.Property(r => r.TargetKey).HasColumnName("target_key").HasMaxLength(100).HasDefaultValue(string.Empty);
            entity.Property(r => r.Channel).HasColumnName("channel").HasMaxLength(100);
            entity.Property(r => r.BucketStart).HasColumnName("bucket_start").HasColumnType("timestamptz");
            entity.Property(r => r.Avg).HasColumnName("avg").HasColumnType("numeric(18,4)");
            entity.Property(r => r.Min).HasColumnName("min").HasColumnType("numeric(18,4)");
            entity.Property(r => r.Max).HasColumnName("max").HasColumnType("numeric(18,4)");
            entity.Property(r => r.Last).HasColumnName("last").HasColumnType("numeric(18,4)");
            entity.Property(r => r.Samples).HasColumnName("samples").HasDefaultValue(0);
            entity.Property(r => r.Failures).HasColumnName("failures").HasDefaultValue(0);
            entity.Property(r => r.WorstStatus).HasColumnName("worst_status").HasDefaultValue(0);
        });

        modelBuilder.Entity<AgentSensorEvent>(entity =>
        {
            entity.ToTable("agent_sensor_events");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AgentId, e.SensorId, e.OccurredAt })
                .HasDatabaseName("ix_sensor_events_agent_sensor_occurred");
            // target_key ENTRA na unicidade: uma coleta multi-volume usa o mesmo
            // runId para todas as amostras do sensor (ver M265).
            entity.HasIndex(e => new { e.AgentId, e.RunId, e.Kind, e.TargetKey })
                .HasDatabaseName("ux_sensor_events_agent_run_kind").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.AgentId).HasColumnName("agent_id");
            entity.Property(e => e.SensorId).HasColumnName("sensor_id");
            entity.Property(e => e.TargetKey).HasColumnName("target_key").HasMaxLength(100).HasDefaultValue(string.Empty);
            entity.Property(e => e.RunId).HasColumnName("run_id");
            entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<int>();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(e => e.Severity).HasColumnName("severity").HasConversion<int?>();
            entity.Property(e => e.PreviousStatus).HasColumnName("previous_status").HasConversion<int?>();
            entity.Property(e => e.PreviousSeverity).HasColumnName("previous_severity").HasConversion<int?>();
            entity.Property(e => e.Message).HasColumnName("message").HasMaxLength(1000);
            entity.Property(e => e.ChannelsJson).HasColumnName("channels_json").HasColumnType("jsonb");
            entity.Property(e => e.ExitCode).HasColumnName("exit_code");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(e => e.AgentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SensorDefinition>().WithMany().HasForeignKey(e => e.SensorId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
