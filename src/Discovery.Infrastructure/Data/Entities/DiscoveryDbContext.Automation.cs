using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Automation & App Store: ScriptDefinitions, ScriptAudits, TaskDefinitions, TaskAudits,
//    ExecutionReports, SyncPingDeliveries, AppApprovalRules, AppApprovalAudits,
//    AppPackages, ChocolateyPackages, WingetPackages, CustomFields ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureAutomation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AutomationScriptDefinition>(entity =>
        {
            entity.ToTable("automation_script_definitions");
            entity.HasKey(s => s.Id);
            entity.HasIndex(s => new { s.ClientId, s.IsActive, s.UpdatedAt }).HasDatabaseName("ix_automation_scripts_client_active_updated");
            entity.HasIndex(s => new { s.Name, s.Version }).HasDatabaseName("ix_automation_scripts_name_version");

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.ClientId).HasColumnName("client_id");
            entity.Property(s => s.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(s => s.Summary).HasColumnName("summary").HasMaxLength(2000);
            entity.Property(s => s.ScriptType).HasColumnName("script_type").HasConversion<int>();
            entity.Property(s => s.Version).HasColumnName("version").HasMaxLength(50);
            entity.Property(s => s.ExecutionFrequency).HasColumnName("execution_frequency").HasMaxLength(100);
            entity.Property(s => s.TriggerModesJson).HasColumnName("trigger_modes_json").HasColumnType("jsonb");
            entity.Property(s => s.Content).HasColumnName("content").HasColumnType("text");
            entity.Property(s => s.ParametersSchemaJson).HasColumnName("parameters_schema_json").HasColumnType("jsonb");
            entity.Property(s => s.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(s => s.IsActive).HasColumnName("is_active");
            entity.Property(s => s.LastUpdatedAt).HasColumnName("last_updated_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(s => s.ClientId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AutomationScriptAudit>(entity =>
        {
            entity.ToTable("automation_script_audits");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.ScriptId, a.ChangedAt }).HasDatabaseName("ix_automation_script_audits_script_changed");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.ScriptId).HasColumnName("script_id");
            entity.Property(a => a.ChangeType).HasColumnName("change_type").HasConversion<int>();
            entity.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(2000);
            entity.Property(a => a.OldValueJson).HasColumnName("old_value_json").HasColumnType("jsonb");
            entity.Property(a => a.NewValueJson).HasColumnName("new_value_json").HasColumnType("jsonb");
            entity.Property(a => a.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            entity.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            entity.Property(a => a.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");

            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(a => a.ScriptId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationTaskDefinition>(entity =>
        {
            entity.ToTable("automation_task_definitions");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.ScopeType, t.ClientId, t.SiteId, t.AgentId }).HasDatabaseName("ix_automation_tasks_scope");
            entity.HasIndex(t => new { t.IsActive, t.UpdatedAt }).HasDatabaseName("ix_automation_tasks_active_updated");

            entity.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(t => t.Description).HasColumnName("description").HasMaxLength(2000);
            entity.Property(t => t.ActionType).HasColumnName("action_type").HasConversion<int>();
            entity.Property(t => t.InstallationType).HasColumnName("installation_type").HasConversion<int?>();
            entity.Property(t => t.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(t => t.ScriptId).HasColumnName("script_id");
            entity.Property(t => t.CommandPayload).HasColumnName("command_payload").HasColumnType("text");
            entity.Property(t => t.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(t => t.ClientId).HasColumnName("client_id");
            entity.Property(t => t.SiteId).HasColumnName("site_id");
            entity.Property(t => t.AgentId).HasColumnName("agent_id");
            entity.Property(t => t.IncludeTagsJson).HasColumnName("include_tags_json").HasColumnType("jsonb");
            entity.Property(t => t.ExcludeTagsJson).HasColumnName("exclude_tags_json").HasColumnType("jsonb");
            entity.Property(t => t.TriggerImmediate).HasColumnName("trigger_immediate");
            entity.Property(t => t.TriggerRecurring).HasColumnName("trigger_recurring");
            entity.Property(t => t.TriggerOnUserLogin).HasColumnName("trigger_on_user_login");
            entity.Property(t => t.TriggerOnAgentCheckIn).HasColumnName("trigger_on_agent_check_in");
            entity.Property(t => t.ScheduleCron).HasColumnName("schedule_cron").HasMaxLength(100);
            entity.Property(t => t.RequiresApproval).HasColumnName("requires_approval");
            entity.Property(t => t.IsActive).HasColumnName("is_active");
            entity.Property(t => t.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            entity.Property(t => t.LastUpdatedAt).HasColumnName("last_updated_at").HasColumnType("timestamptz");
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Site>().WithMany().HasForeignKey(t => t.SiteId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Agent>().WithMany().HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(t => t.ScriptId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AutomationTaskAudit>(entity =>
        {
            entity.ToTable("automation_task_audits");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.TaskId, a.ChangedAt }).HasDatabaseName("ix_automation_task_audits_task_changed");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.TaskId).HasColumnName("task_id");
            entity.Property(a => a.ChangeType).HasColumnName("change_type").HasConversion<int>();
            entity.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(2000);
            entity.Property(a => a.OldValueJson).HasColumnName("old_value_json").HasColumnType("jsonb");
            entity.Property(a => a.NewValueJson).HasColumnName("new_value_json").HasColumnType("jsonb");
            entity.Property(a => a.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            entity.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            entity.Property(a => a.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");

            entity.HasOne<AutomationTaskDefinition>().WithMany().HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationExecutionReport>(entity =>
        {
            entity.ToTable("automation_execution_reports");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.CommandId).IsUnique().HasDatabaseName("ux_automation_execution_reports_command");
            entity.HasIndex(r => new { r.AgentId, r.CreatedAt }).HasDatabaseName("ix_automation_execution_reports_agent_created");

            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.CommandId).HasColumnName("command_id");
            entity.Property(r => r.AgentId).HasColumnName("agent_id");
            entity.Property(r => r.TaskId).HasColumnName("task_id");
            entity.Property(r => r.ScriptId).HasColumnName("script_id");
            entity.Property(r => r.SourceType).HasColumnName("source_type").HasConversion<int>();
            entity.Property(r => r.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(r => r.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            entity.Property(r => r.RequestMetadataJson).HasColumnName("request_metadata_json").HasColumnType("jsonb");
            entity.Property(r => r.AckMetadataJson).HasColumnName("ack_metadata_json").HasColumnType("jsonb");
            entity.Property(r => r.ResultMetadataJson).HasColumnName("result_metadata_json").HasColumnType("jsonb");
            entity.Property(r => r.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            entity.Property(r => r.ResultReceivedAt).HasColumnName("result_received_at").HasColumnType("timestamptz");
            entity.Property(r => r.ExitCode).HasColumnName("exit_code");
            entity.Property(r => r.ErrorMessage).HasColumnName("error_message").HasMaxLength(4000);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(r => r.AgentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AgentCommand>().WithMany().HasForeignKey(r => r.CommandId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AutomationTaskDefinition>().WithMany().HasForeignKey(r => r.TaskId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(r => r.ScriptId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SyncPingDelivery>(entity =>
        {
            entity.ToTable("sync_ping_deliveries");
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => new { d.EventId, d.AgentId, d.Revision }).IsUnique().HasDatabaseName("ux_sync_ping_deliveries_event_agent_revision");
            entity.HasIndex(d => new { d.Status, d.SentAt }).HasDatabaseName("ix_sync_ping_deliveries_status_sent");
            entity.HasIndex(d => new { d.AgentId, d.CreatedAt }).HasDatabaseName("ix_sync_ping_deliveries_agent_created");

            entity.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(d => d.EventId).HasColumnName("event_id");
            entity.Property(d => d.AgentId).HasColumnName("agent_id");
            entity.Property(d => d.Resource).HasColumnName("resource").HasConversion<int>();
            entity.Property(d => d.Revision).HasColumnName("revision").HasMaxLength(255);
            entity.Property(d => d.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(d => d.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");
            entity.Property(d => d.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            entity.Property(d => d.AckMetadataJson).HasColumnName("ack_metadata_json").HasColumnType("jsonb");
            entity.Property(d => d.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
            entity.Property(d => d.ErrorMessage).HasColumnName("error_message").HasMaxLength(1000);
            entity.Property(d => d.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(d => d.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(d => d.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppApprovalRule>(entity =>
        {
            entity.ToTable("app_approval_rules");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.ScopeType, r.ClientId, r.SiteId, r.AgentId, r.InstallationType, r.PackageId }).IsUnique().HasDatabaseName("ux_app_approval_rules_unique");

            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(r => r.ClientId).HasColumnName("client_id");
            entity.Property(r => r.SiteId).HasColumnName("site_id");
            entity.Property(r => r.AgentId).HasColumnName("agent_id");
            entity.Property(r => r.InstallationType).HasColumnName("installation_type").HasConversion<int>();
            entity.Property(r => r.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(r => r.Action).HasColumnName("action").HasConversion<int>();
            entity.Property(r => r.AutoUpdateEnabled).HasColumnName("auto_update_enabled");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(r => r.ClientId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Site>().WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Agent>().WithMany().HasForeignKey(r => r.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppApprovalAudit>(entity =>
        {
            entity.ToTable("app_approval_audits");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.InstallationType, a.PackageId, a.ChangedAt }).HasDatabaseName("ix_app_approval_audits_package_changed");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.RuleId).HasColumnName("rule_id");
            entity.Property(a => a.ChangeType).HasColumnName("change_type").HasConversion<int>();
            entity.Property(a => a.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(a => a.ClientId).HasColumnName("client_id");
            entity.Property(a => a.SiteId).HasColumnName("site_id");
            entity.Property(a => a.AgentId).HasColumnName("agent_id");
            entity.Property(a => a.InstallationType).HasColumnName("installation_type").HasConversion<int>();
            entity.Property(a => a.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(a => a.OldAction).HasColumnName("old_action").HasConversion<int?>();
            entity.Property(a => a.NewAction).HasColumnName("new_action").HasConversion<int?>();
            entity.Property(a => a.OldAutoUpdateEnabled).HasColumnName("old_auto_update_enabled");
            entity.Property(a => a.NewAutoUpdateEnabled).HasColumnName("new_auto_update_enabled");
            entity.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(2000);
            entity.Property(a => a.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            entity.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(a => a.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");

            entity.HasOne<AppApprovalRule>().WithMany().HasForeignKey(a => a.RuleId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppPackage>(entity =>
        {
            entity.ToTable("app_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.InstallationType, p.PackageId }).IsUnique().HasDatabaseName("ux_app_packages_installation_package");

            entity.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(p => p.InstallationType).HasColumnName("installation_type").HasConversion<int>();
            entity.Property(p => p.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(p => p.Name).HasColumnName("name").HasMaxLength(500);
            entity.Property(p => p.Publisher).HasColumnName("publisher").HasMaxLength(500);
            entity.Property(p => p.Version).HasColumnName("version").HasMaxLength(100);
            entity.Property(p => p.Description).HasColumnName("description").HasColumnType("text");
            entity.Property(p => p.IconUrl).HasColumnName("icon_url").HasMaxLength(2000);
            entity.Property(p => p.SiteUrl).HasColumnName("site_url").HasMaxLength(2000);
            entity.Property(p => p.InstallCommand).HasColumnName("install_command").HasMaxLength(1000);
            entity.Property(p => p.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            entity.Property(p => p.FileObjectKey).HasColumnName("file_object_key").HasMaxLength(1000);
            entity.Property(p => p.FileBucket).HasColumnName("file_bucket").HasMaxLength(200);
            entity.Property(p => p.FilePublicUrl).HasColumnName("file_public_url").HasMaxLength(2000);
            entity.Property(p => p.FileContentType).HasColumnName("file_content_type").HasMaxLength(200);
            entity.Property(p => p.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(p => p.FileChecksum).HasColumnName("file_checksum").HasMaxLength(200);
            entity.Property(p => p.SourceGeneratedAt).HasColumnName("source_generated_at").HasColumnType("timestamptz");
            entity.Property(p => p.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt).HasColumnName("synced_at").HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ChocolateyPackage>(entity =>
        {
            entity.ToTable("chocolatey_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PackageId).IsUnique().HasDatabaseName("ux_chocolatey_packages_package_id");

            entity.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(p => p.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(p => p.Name).HasColumnName("name").HasMaxLength(500);
            entity.Property(p => p.Publisher).HasColumnName("publisher").HasMaxLength(500);
            entity.Property(p => p.Version).HasColumnName("version").HasMaxLength(100);
            entity.Property(p => p.Description).HasColumnName("description").HasColumnType("text");
            entity.Property(p => p.Homepage).HasColumnName("homepage").HasMaxLength(2000);
            entity.Property(p => p.LicenseUrl).HasColumnName("license_url").HasMaxLength(2000);
            entity.Property(p => p.Tags).HasColumnName("tags").HasMaxLength(2000);
            entity.Property(p => p.DownloadCount).HasColumnName("download_count");
            entity.Property(p => p.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt).HasColumnName("synced_at").HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<WingetPackage>(entity =>
        {
            entity.ToTable("winget_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PackageId).IsUnique().HasDatabaseName("ux_winget_packages_package_id");

            entity.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(p => p.PackageId).HasColumnName("package_id").HasMaxLength(300);
            entity.Property(p => p.Name).HasColumnName("name").HasMaxLength(500);
            entity.Property(p => p.Publisher).HasColumnName("publisher").HasMaxLength(500);
            entity.Property(p => p.Version).HasColumnName("version").HasMaxLength(100);
            entity.Property(p => p.Description).HasColumnName("description").HasColumnType("text");
            entity.Property(p => p.Homepage).HasColumnName("homepage").HasMaxLength(2000);
            entity.Property(p => p.License).HasColumnName("license").HasMaxLength(500);
            entity.Property(p => p.Category).HasColumnName("category").HasMaxLength(250);
            entity.Property(p => p.Icon).HasColumnName("icon").HasMaxLength(2000);
            entity.Property(p => p.InstallCommand).HasColumnName("install_command").HasMaxLength(1000);
            entity.Property(p => p.Tags).HasColumnName("tags").HasColumnType("text");
            entity.Property(p => p.InstallerUrlsJson).HasColumnName("installer_urls_json").HasColumnType("text");
            entity.Property(p => p.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamptz");
            entity.Property(p => p.SourceGeneratedAt).HasColumnName("source_generated_at").HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt).HasColumnName("synced_at").HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });
    }
}
