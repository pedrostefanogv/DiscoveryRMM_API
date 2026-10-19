using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Patches: inventário de Windows Update, aprovações, políticas e enforcement ──

public partial class DiscoveryDbContext
{
    static partial void ConfigurePatches(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentWindowsUpdate>(entity =>
        {
            entity.ToTable("agent_windows_updates");
            entity.HasKey(update => update.Id);

            entity.HasIndex(update => new { update.AgentId, update.UpdateKey })
                .IsUnique()
                .HasDatabaseName("ux_agent_windows_updates_agent_update");
            entity.HasIndex(update => new { update.AgentId, update.IsPending, update.IsPresent })
                .HasDatabaseName("ix_agent_windows_updates_agent_pending");
            entity.HasIndex(update => update.GroupKey)
                .HasDatabaseName("ix_agent_windows_updates_group_key");
            entity.HasIndex(update => new { update.AgentId, update.GroupKey })
                .HasDatabaseName("ix_agent_windows_updates_agent_group");
            entity.HasIndex(update => update.ApprovalState)
                .HasDatabaseName("ix_agent_windows_updates_approval_state");
            entity.HasIndex(update => update.Status)
                .HasDatabaseName("ix_agent_windows_updates_status");

            entity.Property(update => update.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(update => update.AgentId).HasColumnName("agent_id");
            entity.Property(update => update.UpdateKey).HasColumnName("update_key").HasMaxLength(120);
            entity.Property(update => update.GroupKey).HasColumnName("group_key").HasMaxLength(120);
            entity.Property(update => update.KbIds).HasColumnName("kb_ids").HasColumnType("jsonb");
            entity.Property(update => update.UpdateId).HasColumnName("update_id").HasMaxLength(64);
            entity.Property(update => update.Revision).HasColumnName("revision");
            entity.Property(update => update.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(update => update.Description).HasColumnName("description").HasMaxLength(2000);
            entity.Property(update => update.Classification).HasColumnName("classification").HasMaxLength(80);
            entity.Property(update => update.Categories).HasColumnName("categories").HasColumnType("jsonb");
            entity.Property(update => update.Severity).HasColumnName("severity").HasMaxLength(40);
            entity.Property(update => update.IsPending).HasColumnName("is_pending");
            entity.Property(update => update.IsInstalled).HasColumnName("is_installed");
            entity.Property(update => update.IsHidden).HasColumnName("is_hidden");
            entity.Property(update => update.IsDownloaded).HasColumnName("is_downloaded");
            entity.Property(update => update.IsMandatory).HasColumnName("is_mandatory");
            entity.Property(update => update.RequiresReboot).HasColumnName("requires_reboot");
            entity.Property(update => update.MaxDownloadSize).HasColumnName("max_download_size");
            entity.Property(update => update.SupportUrl).HasColumnName("support_url").HasMaxLength(1000);
            entity.Property(update => update.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            entity.Property(update => update.LastDeployedAt).HasColumnName("last_deployed_at").HasColumnType("timestamptz");
            entity.Property(update => update.InstalledAt).HasColumnName("installed_at").HasColumnType("timestamptz");
            entity.Property(update => update.ReportedAt).HasColumnName("reported_at").HasColumnType("timestamptz");
            entity.Property(update => update.InstallStartedAt).HasColumnName("install_started_at").HasColumnType("timestamptz");
            entity.Property(update => update.InstallResultCode).HasColumnName("install_result_code");
            entity.Property(update => update.InstallError).HasColumnName("install_error").HasMaxLength(1000);
            entity.Property(update => update.ApprovalState).HasColumnName("approval_state");
            entity.Property(update => update.ApprovalSource).HasColumnName("approval_source").HasMaxLength(30);
            entity.Property(update => update.Status).HasColumnName("status");
            entity.Property(update => update.Source).HasColumnName("source").HasMaxLength(20);
            entity.Property(update => update.Raw).HasColumnName("raw").HasColumnType("jsonb");
            entity.Property(update => update.FirstSeenAt).HasColumnName("first_seen_at").HasColumnType("timestamptz");
            entity.Property(update => update.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");
            entity.Property(update => update.IsPresent).HasColumnName("is_present");
            entity.Property(update => update.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(update => update.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(update => update.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentWindowsUpdateSnapshot>(entity =>
        {
            entity.ToTable("agent_windows_update_snapshots");
            entity.HasKey(snapshot => snapshot.AgentId);
            entity.Property(snapshot => snapshot.AgentId).HasColumnName("agent_id").ValueGeneratedNever();
            entity.Property(snapshot => snapshot.CollectedAt).HasColumnName("collected_at").HasColumnType("timestamptz");
            entity.Property(snapshot => snapshot.ScanStatus).HasColumnName("scan_status").HasMaxLength(20);
            entity.Property(snapshot => snapshot.ScanDurationMs).HasColumnName("scan_duration_ms");
            entity.Property(snapshot => snapshot.ScanError).HasColumnName("scan_error").HasMaxLength(1000);
            entity.Property(snapshot => snapshot.PendingCount).HasColumnName("pending_count");
            entity.Property(snapshot => snapshot.InstalledCount).HasColumnName("installed_count");
            entity.Property(snapshot => snapshot.DeniedCount).HasColumnName("denied_count");
            entity.Property(snapshot => snapshot.PendingReboot).HasColumnName("pending_reboot");
            entity.Property(snapshot => snapshot.PendingRebootReasons).HasColumnName("pending_reboot_reasons").HasColumnType("jsonb");
            entity.Property(snapshot => snapshot.RebootCheckedAt).HasColumnName("reboot_checked_at").HasColumnType("timestamptz");
            // Sonda de estado (bloco opcional "state" do envelope) — M238.
            entity.Property(snapshot => snapshot.ServiceState).HasColumnName("service_state").HasMaxLength(20);
            entity.Property(snapshot => snapshot.WuOnline).HasColumnName("wu_online");
            entity.Property(snapshot => snapshot.PolicyManaged).HasColumnName("policy_managed");
            entity.Property(snapshot => snapshot.LastInstallSuccessAt).HasColumnName("last_install_success_at").HasColumnType("timestamptz");
            entity.Property(snapshot => snapshot.DownloadInProgress).HasColumnName("download_in_progress");
            entity.Property(snapshot => snapshot.StateHash).HasColumnName("state_hash").HasMaxLength(80);
            entity.Property(snapshot => snapshot.StateChangedAt).HasColumnName("state_changed_at").HasColumnType("timestamptz");
            entity.Property(snapshot => snapshot.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PatchApproval>(entity =>
        {
            entity.ToTable("patch_approvals");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => new { rule.ScopeType, rule.ScopeId, rule.UpdateKey })
                .IsUnique()
                .HasDatabaseName("ux_patch_approvals_scope_update");
            entity.HasIndex(rule => rule.UpdateKey).HasDatabaseName("ix_patch_approvals_update_key");

            entity.Property(rule => rule.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(rule => rule.ScopeType).HasColumnName("scope_type");
            entity.Property(rule => rule.ScopeId).HasColumnName("scope_id");
            entity.Property(rule => rule.ClientId).HasColumnName("client_id");
            entity.Property(rule => rule.SiteId).HasColumnName("site_id");
            entity.Property(rule => rule.AgentId).HasColumnName("agent_id");
            entity.Property(rule => rule.UpdateKey).HasColumnName("update_key").HasMaxLength(120);
            entity.Property(rule => rule.KbId).HasColumnName("kb_id").HasMaxLength(20);
            entity.Property(rule => rule.Action).HasColumnName("action");
            entity.Property(rule => rule.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(rule => rule.CreatedBy).HasColumnName("created_by").HasMaxLength(200);
            entity.Property(rule => rule.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200);
            entity.Property(rule => rule.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(rule => rule.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<PatchApprovalAudit>(entity =>
        {
            entity.ToTable("patch_approval_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => audit.ChangedAt).HasDatabaseName("ix_patch_approval_audits_changed_at");
            entity.HasIndex(audit => audit.UpdateKey).HasDatabaseName("ix_patch_approval_audits_update_key");

            entity.Property(audit => audit.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(audit => audit.RuleId).HasColumnName("rule_id");
            entity.Property(audit => audit.ChangeType).HasColumnName("change_type");
            entity.Property(audit => audit.ScopeType).HasColumnName("scope_type");
            entity.Property(audit => audit.ScopeId).HasColumnName("scope_id");
            entity.Property(audit => audit.ClientId).HasColumnName("client_id");
            entity.Property(audit => audit.SiteId).HasColumnName("site_id");
            entity.Property(audit => audit.AgentId).HasColumnName("agent_id");
            entity.Property(audit => audit.UpdateKey).HasColumnName("update_key").HasMaxLength(120);
            entity.Property(audit => audit.KbId).HasColumnName("kb_id").HasMaxLength(20);
            entity.Property(audit => audit.OldAction).HasColumnName("old_action");
            entity.Property(audit => audit.NewAction).HasColumnName("new_action");
            entity.Property(audit => audit.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(audit => audit.ChangedBy).HasColumnName("changed_by").HasMaxLength(200);
            entity.Property(audit => audit.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(audit => audit.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<PatchPolicy>(entity =>
        {
            entity.ToTable("patch_policies");
            entity.HasKey(policy => policy.Id);
            entity.HasIndex(policy => new { policy.ScopeType, policy.ScopeId })
                .IsUnique()
                .HasDatabaseName("ux_patch_policies_scope");

            entity.Property(policy => policy.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(policy => policy.ScopeType).HasColumnName("scope_type");
            entity.Property(policy => policy.ScopeId).HasColumnName("scope_id");
            entity.Property(policy => policy.ClientId).HasColumnName("client_id");
            entity.Property(policy => policy.SiteId).HasColumnName("site_id");
            entity.Property(policy => policy.EnforcementMode).HasColumnName("enforcement_mode");
            entity.Property(policy => policy.AutoApproveClassifications).HasColumnName("auto_approve_classifications").HasColumnType("jsonb");
            entity.Property(policy => policy.AutoApproveSeverities).HasColumnName("auto_approve_severities").HasColumnType("jsonb");
            entity.Property(policy => policy.AutoInstallEnabled).HasColumnName("auto_install_enabled");
            entity.Property(policy => policy.MaintenanceWindowJson).HasColumnName("maintenance_window_json").HasColumnType("jsonb");
            entity.Property(policy => policy.RebootPolicy).HasColumnName("reboot_policy");
            entity.Property(policy => policy.RebootAllowed).HasColumnName("reboot_allowed");
            entity.Property(policy => policy.MaxInstallsPerRun).HasColumnName("max_installs_per_run");
            entity.Property(policy => policy.HideDeniedUpdates).HasColumnName("hide_denied_updates");
            entity.Property(policy => policy.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200);
            entity.Property(policy => policy.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(policy => policy.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<PatchOsState>(entity =>
        {
            entity.ToTable("patch_os_states");
            entity.HasKey(state => state.Id);
            entity.HasIndex(state => state.AgentId).HasDatabaseName("ix_patch_os_states_agent");

            entity.Property(state => state.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(state => state.AgentId).HasColumnName("agent_id");
            entity.Property(state => state.CommandId).HasColumnName("command_id");
            entity.Property(state => state.AppliedBy).HasColumnName("applied_by").HasMaxLength(200);
            entity.Property(state => state.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz");
            entity.Property(state => state.PreviousJson).HasColumnName("previous_json").HasColumnType("jsonb");
            entity.Property(state => state.AppliedJson).HasColumnName("applied_json").HasColumnType("jsonb");
            entity.Property(state => state.RevertedAt).HasColumnName("reverted_at").HasColumnType("timestamptz");
            entity.Property(state => state.RevertedBy).HasColumnName("reverted_by").HasMaxLength(200);

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(state => state.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}