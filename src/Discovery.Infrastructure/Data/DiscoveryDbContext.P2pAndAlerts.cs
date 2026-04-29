using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// P2P, Auto-Ticket, Monitoring, Agent Alerts, Ticket escalation/watch/remote-session/automation-link, and SLA calendar configurations.
/// </summary>
public partial class DiscoveryDbContext
{
    static partial void ConfigureP2pEntities(ModelBuilder modelBuilder)
    {
        // ── P2P ──────────────────────────────────────────────────────────────

        modelBuilder.Entity<P2pAgentTelemetry>(e =>
        {
            e.ToTable("p2p_agent_telemetry");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(t => t.AgentId).HasColumnName("agent_id");
            e.Property(t => t.SiteId).HasColumnName("site_id");
            e.Property(t => t.ClientId).HasColumnName("client_id");
            e.Property(t => t.CollectedAt).HasColumnName("collected_at").HasColumnType("timestamptz");
            e.Property(t => t.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            e.Property(t => t.PublishedArtifacts).HasColumnName("published_artifacts");
            e.Property(t => t.ReplicationsStarted).HasColumnName("replications_started");
            e.Property(t => t.ReplicationsSucceeded).HasColumnName("replications_succeeded");
            e.Property(t => t.ReplicationsFailed).HasColumnName("replications_failed");
            e.Property(t => t.BytesServed).HasColumnName("bytes_served");
            e.Property(t => t.BytesDownloaded).HasColumnName("bytes_downloaded");
            e.Property(t => t.QueuedReplications).HasColumnName("queued_replications");
            e.Property(t => t.ActiveReplications).HasColumnName("active_replications");
            e.Property(t => t.AutoDistributionRuns).HasColumnName("auto_distribution_runs");
            e.Property(t => t.CatalogRefreshRuns).HasColumnName("catalog_refresh_runs");
            e.Property(t => t.ChunkedDownloads).HasColumnName("chunked_downloads");
            e.Property(t => t.ChunksDownloaded).HasColumnName("chunks_downloaded");
            e.Property(t => t.PlanTotalAgents).HasColumnName("plan_total_agents");
            e.Property(t => t.PlanConfiguredPercent).HasColumnName("plan_configured_percent");
            e.Property(t => t.PlanMinSeeds).HasColumnName("plan_min_seeds");
            e.Property(t => t.PlanSelectedSeeds).HasColumnName("plan_selected_seeds");
            e.HasIndex(t => new { t.AgentId, t.CollectedAt }).HasDatabaseName("ix_p2p_telemetry_agent_time");
            e.HasIndex(t => new { t.SiteId, t.CollectedAt }).HasDatabaseName("ix_p2p_telemetry_site_time");
            e.HasIndex(t => new { t.ClientId, t.CollectedAt }).HasDatabaseName("ix_p2p_telemetry_client_time");
        });

        modelBuilder.Entity<P2pArtifactPresence>(e =>
        {
            e.ToTable("p2p_artifact_presence");
            e.HasKey(p => new { p.ArtifactId, p.AgentId });
            e.Property(p => p.ArtifactId).HasColumnName("artifact_id").HasMaxLength(512);
            e.Property(p => p.AgentId).HasColumnName("agent_id");
            e.Property(p => p.SiteId).HasColumnName("site_id");
            e.Property(p => p.ClientId).HasColumnName("client_id");
            e.Property(p => p.ArtifactName).HasColumnName("artifact_name").HasMaxLength(260);
            e.Property(p => p.IdIsSynthetic).HasColumnName("id_is_synthetic");
            e.Property(p => p.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");
            e.HasIndex(p => new { p.ArtifactId, p.LastSeenAt }).HasDatabaseName("ix_p2p_presence_artifact_time");
            e.HasIndex(p => new { p.SiteId, p.LastSeenAt }).HasDatabaseName("ix_p2p_presence_site_time");
            e.HasIndex(p => new { p.ClientId, p.LastSeenAt }).HasDatabaseName("ix_p2p_presence_client_time");
        });

        modelBuilder.Entity<P2pSeedPlan>(e =>
        {
            e.ToTable("p2p_seed_plan");
            e.HasKey(p => p.SiteId);
            e.Property(p => p.SiteId).HasColumnName("site_id").ValueGeneratedNever();
            e.Property(p => p.ClientId).HasColumnName("client_id");
            e.Property(p => p.TotalAgents).HasColumnName("total_agents");
            e.Property(p => p.ConfiguredPercent).HasColumnName("configured_percent");
            e.Property(p => p.MinSeeds).HasColumnName("min_seeds");
            e.Property(p => p.SelectedSeeds).HasColumnName("selected_seeds");
            e.Property(p => p.GeneratedAt).HasColumnName("generated_at").HasColumnType("timestamptz");
            e.HasIndex(p => p.ClientId).HasDatabaseName("ix_p2p_seed_plan_client");
        });

        modelBuilder.Entity<AgentP2pBootstrap>(e =>
        {
            e.ToTable("agent_p2p_bootstraps");
            e.HasKey(b => b.AgentId);
            e.Property(b => b.AgentId).HasColumnName("agent_id").ValueGeneratedNever();
            e.Property(b => b.ClientId).HasColumnName("client_id");
            e.Property(b => b.PeerId).HasColumnName("peer_id").HasMaxLength(128);
            e.Property(b => b.AddrsJson).HasColumnName("addrs_json").HasMaxLength(1024);
            e.Property(b => b.Port).HasColumnName("port");
            e.Property(b => b.LastHeartbeatAt).HasColumnName("last_heartbeat_at").HasColumnType("timestamptz");
            e.HasIndex(b => b.ClientId).HasDatabaseName("ix_agent_p2p_bootstraps_client_id");
        });

        // ── Auto Ticket Rules ────────────────────────────────────────────────

        modelBuilder.Entity<AutoTicketRule>(entity =>
        {
            entity.ToTable("auto_ticket_rules");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => new { rule.ScopeLevel, rule.IsEnabled }).HasDatabaseName("ix_auto_ticket_rules_scope_enabled");
            entity.HasIndex(rule => rule.AlertCodeFilter).HasDatabaseName("ix_auto_ticket_rules_alert_code");
            entity.Property(rule => rule.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(rule => rule.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(rule => rule.IsEnabled).HasColumnName("is_enabled");
            entity.Property(rule => rule.PriorityOrder).HasColumnName("priority_order");
            entity.Property(rule => rule.ScopeLevel).HasColumnName("scope_level").HasConversion<int>();
            entity.Property(rule => rule.ScopeId).HasColumnName("scope_id");
            entity.Property(rule => rule.AlertCodeFilter).HasColumnName("alert_code_filter").HasMaxLength(200);
            entity.Property(rule => rule.SourceFilter).HasColumnName("source_filter").HasConversion<int?>();
            entity.Property(rule => rule.SeverityMin).HasColumnName("severity_min").HasConversion<int?>();
            entity.Property(rule => rule.SeverityMax).HasColumnName("severity_max").HasConversion<int?>();
            entity.Property(rule => rule.MatchLabelsAnyJson).HasColumnName("match_labels_any_json").HasColumnType("jsonb");
            entity.Property(rule => rule.MatchLabelsAllJson).HasColumnName("match_labels_all_json").HasColumnType("jsonb");
            entity.Property(rule => rule.ExcludeLabelsJson).HasColumnName("exclude_labels_json").HasColumnType("jsonb");
            entity.Property(rule => rule.PayloadPredicateJson).HasColumnName("payload_predicate_json").HasColumnType("jsonb");
            entity.Property(rule => rule.Action).HasColumnName("action").HasConversion<int>();
            entity.Property(rule => rule.TargetDepartmentId).HasColumnName("target_department_id");
            entity.Property(rule => rule.TargetWorkflowProfileId).HasColumnName("target_workflow_profile_id");
            entity.Property(rule => rule.TargetCategory).HasColumnName("target_category").HasMaxLength(100);
            entity.Property(rule => rule.TargetPriority).HasColumnName("target_priority").HasConversion<int?>();
            entity.Property(rule => rule.DedupWindowMinutes).HasColumnName("dedup_window_minutes");
            entity.Property(rule => rule.CooldownMinutes).HasColumnName("cooldown_minutes");
            entity.Property(rule => rule.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(rule => rule.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AlertCorrelationLock>(entity =>
        {
            entity.ToTable("alert_correlation_locks");
            entity.HasKey(correlationLock => correlationLock.DedupKey);
            entity.Property(correlationLock => correlationLock.DedupKey).HasColumnName("dedup_key").HasMaxLength(500).ValueGeneratedNever();
            entity.Property(correlationLock => correlationLock.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(correlationLock => correlationLock.LastTicketId).HasColumnName("last_ticket_id");
            entity.Property(correlationLock => correlationLock.LastAlertAt).HasColumnName("last_alert_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AutoTicketRuleExecution>(entity =>
        {
            entity.ToTable("auto_ticket_rule_executions");
            entity.HasKey(execution => execution.Id);
            entity.HasIndex(execution => execution.MonitoringEventId).HasDatabaseName("ix_auto_ticket_rule_executions_event_id");
            entity.HasIndex(execution => new { execution.AgentId, execution.EvaluatedAt }).HasDatabaseName("ix_auto_ticket_rule_executions_agent_evaluated");
            entity.Property(execution => execution.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(execution => execution.RuleId).HasColumnName("rule_id");
            entity.Property(execution => execution.MonitoringEventId).HasColumnName("monitoring_event_id");
            entity.Property(execution => execution.AgentId).HasColumnName("agent_id");
            entity.Property(execution => execution.EvaluatedAt).HasColumnName("evaluated_at").HasColumnType("timestamptz");
            entity.Property(execution => execution.Decision).HasColumnName("decision").HasConversion<int>();
            entity.Property(execution => execution.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(execution => execution.CreatedTicketId).HasColumnName("created_ticket_id");
            entity.Property(execution => execution.DedupKey).HasColumnName("dedup_key").HasMaxLength(500);
            entity.Property(execution => execution.DedupHit).HasColumnName("dedup_hit");
            entity.Property(execution => execution.PayloadSnapshotJson).HasColumnName("payload_snapshot_json").HasColumnType("jsonb");
        });

        modelBuilder.Entity<AgentMonitoringEvent>(entity =>
        {
            entity.ToTable("agent_monitoring_events");
            entity.HasKey(monitoringEvent => monitoringEvent.Id);
            entity.HasIndex(monitoringEvent => new { monitoringEvent.AgentId, monitoringEvent.AlertCode, monitoringEvent.OccurredAt }).HasDatabaseName("ix_monitoring_events_agent_code_occurred");
            entity.HasIndex(monitoringEvent => new { monitoringEvent.ClientId, monitoringEvent.OccurredAt }).HasDatabaseName("ix_monitoring_events_client_occurred");
            entity.Property(monitoringEvent => monitoringEvent.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(monitoringEvent => monitoringEvent.ClientId).HasColumnName("client_id");
            entity.Property(monitoringEvent => monitoringEvent.SiteId).HasColumnName("site_id");
            entity.Property(monitoringEvent => monitoringEvent.AgentId).HasColumnName("agent_id");
            entity.Property(monitoringEvent => monitoringEvent.AlertCode).HasColumnName("alert_code").HasMaxLength(200);
            entity.Property(monitoringEvent => monitoringEvent.Severity).HasColumnName("severity").HasConversion<int>();
            entity.Property(monitoringEvent => monitoringEvent.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(monitoringEvent => monitoringEvent.Message).HasColumnName("message").HasMaxLength(2000);
            entity.Property(monitoringEvent => monitoringEvent.MetricKey).HasColumnName("metric_key").HasMaxLength(200);
            entity.Property(monitoringEvent => monitoringEvent.MetricValue).HasColumnName("metric_value").HasPrecision(18, 4);
            entity.Property(monitoringEvent => monitoringEvent.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(monitoringEvent => monitoringEvent.LabelsSnapshotJson).HasColumnName("labels_snapshot_json").HasColumnType("jsonb");
            entity.Property(monitoringEvent => monitoringEvent.Source).HasColumnName("source").HasConversion<int>();
            entity.Property(monitoringEvent => monitoringEvent.SourceRefId).HasColumnName("source_ref_id");
            entity.Property(monitoringEvent => monitoringEvent.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(monitoringEvent => monitoringEvent.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            entity.Property(monitoringEvent => monitoringEvent.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        // ── Agent Alerts (PSADT) ─────────────────────────────────────────────

        modelBuilder.Entity<AgentAlertDefinition>(entity =>
        {
            entity.ToTable("agent_alert_definitions");
            entity.HasKey(alert => alert.Id);
            entity.HasIndex(alert => alert.Status).HasDatabaseName("ix_agent_alerts_status");
            entity.HasIndex(alert => new { alert.Status, alert.ScheduledAt }).HasDatabaseName("ix_agent_alerts_status_scheduled_at");
            entity.HasIndex(alert => new { alert.ScopeType, alert.ScopeClientId }).HasDatabaseName("ix_agent_alerts_scope_client");
            entity.HasIndex(alert => new { alert.ScopeType, alert.ScopeSiteId }).HasDatabaseName("ix_agent_alerts_scope_site");
            entity.HasIndex(alert => alert.ScopeAgentId).HasDatabaseName("ix_agent_alerts_scope_agent");
            entity.HasIndex(alert => alert.TicketId).HasDatabaseName("ix_agent_alerts_ticket_id");
            entity.Property(alert => alert.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(alert => alert.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(alert => alert.Message).HasColumnName("message").HasMaxLength(2000);
            entity.Property(alert => alert.AlertType).HasColumnName("alert_type").HasConversion<int>();
            entity.Property(alert => alert.TimeoutSeconds).HasColumnName("timeout_seconds");
            entity.Property(alert => alert.ActionsJson).HasColumnName("actions_json").HasColumnType("jsonb");
            entity.Property(alert => alert.DefaultAction).HasColumnName("default_action").HasMaxLength(100);
            entity.Property(alert => alert.Icon).HasColumnName("icon").HasMaxLength(50);
            entity.Property(alert => alert.ScopeType).HasColumnName("scope_type").HasConversion<int>();
            entity.Property(alert => alert.ScopeAgentId).HasColumnName("scope_agent_id");
            entity.Property(alert => alert.ScopeSiteId).HasColumnName("scope_site_id");
            entity.Property(alert => alert.ScopeClientId).HasColumnName("scope_client_id");
            entity.Property(alert => alert.ScopeLabelName).HasColumnName("scope_label_name").HasMaxLength(120);
            entity.Property(alert => alert.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(alert => alert.ScheduledAt).HasColumnName("scheduled_at").HasColumnType("timestamptz");
            entity.Property(alert => alert.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(alert => alert.DispatchedAt).HasColumnName("dispatched_at").HasColumnType("timestamptz");
            entity.Property(alert => alert.DispatchedCount).HasColumnName("dispatched_count");
            entity.Property(alert => alert.TicketId).HasColumnName("ticket_id");
            entity.Property(alert => alert.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(alert => alert.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(alert => alert.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        // ── Ticket: Alerts, Escalation, Watchers, Remote Sessions, Automation Links ──

        modelBuilder.Entity<TicketAlertRule>(entity =>
        {
            entity.ToTable("ticket_alert_rules");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.WorkflowStateId).HasColumnName("workflow_state_id");
            entity.Property(r => r.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(r => r.Message).HasColumnName("message").HasMaxLength(2000);
            entity.Property(r => r.AlertType).HasColumnName("alert_type").HasConversion<int>();
            entity.Property(r => r.TimeoutSeconds).HasColumnName("timeout_seconds");
            entity.Property(r => r.ActionsJson).HasColumnName("actions_json").HasColumnType("jsonb");
            entity.Property(r => r.DefaultAction).HasColumnName("default_action").HasMaxLength(100);
            entity.Property(r => r.Icon).HasColumnName("icon").HasMaxLength(50);
            entity.Property(r => r.ScopePreference).HasColumnName("scope_preference").HasConversion<int>();
            entity.Property(r => r.IsEnabled).HasColumnName("is_enabled");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(r => r.WorkflowStateId).HasDatabaseName("ix_ticket_alert_rules_workflow_state_id");
            entity.HasIndex(r => r.IsEnabled).HasDatabaseName("ix_ticket_alert_rules_is_enabled");
        });

        modelBuilder.Entity<TicketEscalationRule>(entity =>
        {
            entity.ToTable("ticket_escalation_rules");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.WorkflowProfileId).HasColumnName("workflow_profile_id");
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(r => r.TriggerAtSlaPercent).HasColumnName("trigger_at_sla_percent").HasDefaultValue(0);
            entity.Property(r => r.TriggerAtHoursBefore).HasColumnName("trigger_at_hours_before").HasDefaultValue(0);
            entity.Property(r => r.ReassignToUserId).HasColumnName("reassign_to_user_id");
            entity.Property(r => r.ReassignToDepartmentId).HasColumnName("reassign_to_department_id");
            entity.Property(r => r.BumpPriority).HasColumnName("bump_priority").HasDefaultValue(false);
            entity.Property(r => r.NotifyAssignee).HasColumnName("notify_assignee").HasDefaultValue(true);
            entity.Property(r => r.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(r => r.WorkflowProfileId).HasDatabaseName("ix_ticket_escalation_rules_profile_id");
            entity.HasIndex(r => r.IsActive).HasDatabaseName("ix_ticket_escalation_rules_is_active");
        });

        modelBuilder.Entity<TicketWatcher>(entity =>
        {
            entity.ToTable("ticket_watchers");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(w => w.TicketId).HasColumnName("ticket_id");
            entity.Property(w => w.UserId).HasColumnName("user_id");
            entity.Property(w => w.AddedBy).HasColumnName("added_by").HasMaxLength(255);
            entity.Property(w => w.AddedAt).HasColumnName("added_at").HasColumnType("timestamptz");
            entity.HasIndex(w => w.TicketId).HasDatabaseName("ix_ticket_watchers_ticket_id");
            entity.HasIndex(w => new { w.TicketId, w.UserId }).IsUnique().HasDatabaseName("uq_ticket_watchers_ticket_user");
        });

        modelBuilder.Entity<TicketRemoteSession>(entity =>
        {
            entity.ToTable("ticket_remote_sessions");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.TicketId).HasColumnName("ticket_id");
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.MeshNodeId).HasColumnName("mesh_node_id").HasMaxLength(512);
            entity.Property(s => s.SessionUrl).HasColumnName("session_url").HasMaxLength(2048);
            entity.Property(s => s.StartedBy).HasColumnName("started_by").HasMaxLength(255);
            entity.Property(s => s.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(s => s.EndedAt).HasColumnName("ended_at").HasColumnType("timestamptz");
            entity.Property(s => s.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(s => s.Note).HasColumnName("note").HasMaxLength(2000);
            entity.HasIndex(s => s.TicketId).HasDatabaseName("ix_ticket_remote_sessions_ticket_id");
            entity.HasOne(s => s.Ticket).WithMany().HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketAutomationLink>(entity =>
        {
            entity.ToTable("ticket_automation_links");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(l => l.TicketId).HasColumnName("ticket_id");
            entity.Property(l => l.AutomationTaskDefinitionId).HasColumnName("automation_task_definition_id");
            entity.Property(l => l.Status).HasColumnName("status");
            entity.Property(l => l.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
            entity.Property(l => l.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(255);
            entity.Property(l => l.Note).HasColumnName("note").HasMaxLength(2000);
            entity.Property(l => l.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
            entity.Property(l => l.ReviewedAt).HasColumnName("reviewed_at").HasColumnType("timestamptz");
            entity.HasIndex(l => l.TicketId).HasDatabaseName("ix_ticket_automation_links_ticket_id");
            entity.HasOne(l => l.Ticket).WithMany().HasForeignKey(l => l.TicketId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.AutomationTask).WithMany().HasForeignKey(l => l.AutomationTaskDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── SLA Calendars ────────────────────────────────────────────────────

        modelBuilder.Entity<SlaCalendar>(entity =>
        {
            entity.ToTable("sla_calendars");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(c => c.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(c => c.ClientId).HasColumnName("client_id");
            entity.Property(c => c.Timezone).HasColumnName("timezone").HasMaxLength(100);
            entity.Property(c => c.WorkDayStartHour).HasColumnName("work_day_start_hour");
            entity.Property(c => c.WorkDayEndHour).HasColumnName("work_day_end_hour");
            entity.Property(c => c.WorkDaysJson).HasColumnName("work_days_json").HasMaxLength(50);
            entity.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(c => c.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasMany(c => c.Holidays).WithOne(h => h.Calendar).HasForeignKey(h => h.CalendarId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SlaCalendarHoliday>(entity =>
        {
            entity.ToTable("sla_calendar_holidays");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(h => h.CalendarId).HasColumnName("calendar_id");
            entity.Property(h => h.Date).HasColumnName("date").HasColumnType("date");
            entity.Property(h => h.Name).HasColumnName("name").HasMaxLength(255);
            entity.HasIndex(h => h.CalendarId).HasDatabaseName("ix_sla_calendar_holidays_calendar_id");
        });
    }
}
