using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Tickets & Workflow: Ticket, TicketComment, WorkflowState, WorkflowTransition,
//    Department, WorkflowProfile, TicketActivityLog, TicketSavedView ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureTickets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("tickets");
            entity.HasKey(ticket => ticket.Id);
            entity.HasIndex(ticket => ticket.ClientId).HasDatabaseName("ix_tickets_client_id");

            entity.Property(ticket => ticket.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(ticket => ticket.ClientId).HasColumnName("client_id");
            entity.Property(ticket => ticket.SiteId).HasColumnName("site_id");
            entity.Property(ticket => ticket.AgentId).HasColumnName("agent_id");
            entity.Property(ticket => ticket.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(ticket => ticket.Description).HasColumnName("description");
            entity.Property(ticket => ticket.WorkflowStateId).HasColumnName("workflow_state_id");
            entity.Property(ticket => ticket.Priority).HasColumnName("priority").HasConversion<int>();
            entity.Property(ticket => ticket.DepartmentId).HasColumnName("department_id");
            entity.Property(ticket => ticket.WorkflowProfileId).HasColumnName("workflow_profile_id");
            entity.Property(ticket => ticket.AssignedToUserId).HasColumnName("assigned_to_user_id");
            entity.Property(ticket => ticket.SlaExpiresAt).HasColumnName("sla_expires_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.SlaBreached).HasColumnName("sla_breached");
            entity.Property(ticket => ticket.SlaFirstResponseExpiresAt).HasColumnName("sla_first_response_expires_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.FirstRespondedAt).HasColumnName("first_responded_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.SlaPausedSeconds).HasColumnName("sla_paused_seconds").HasDefaultValue(0);
            entity.Property(ticket => ticket.SlaHoldStartedAt).HasColumnName("sla_hold_started_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.Rating).HasColumnName("rating");
            entity.Property(ticket => ticket.RatedAt).HasColumnName("rated_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.RatedBy).HasColumnName("rated_by").HasMaxLength(255);
            entity.Property(ticket => ticket.Category).HasColumnName("category").HasMaxLength(100);
            entity.Property(ticket => ticket.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(ticket => ticket.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>().WithMany().HasForeignKey(ticket => ticket.SiteId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>().WithMany().HasForeignKey(ticket => ticket.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Department>().WithMany().HasForeignKey(ticket => ticket.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowProfile>().WithMany().HasForeignKey(ticket => ticket.WorkflowProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TicketComment>(entity =>
        {
            entity.ToTable("ticket_comments");
            entity.HasKey(comment => comment.Id);
            entity.HasIndex(comment => comment.TicketId).HasDatabaseName("ix_ticket_comments_ticket_id");

            entity.Property(comment => comment.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(comment => comment.TicketId).HasColumnName("ticket_id");
            entity.Property(comment => comment.Author).HasColumnName("author").HasMaxLength(200);
            entity.Property(comment => comment.Content).HasColumnName("content");
            entity.Property(comment => comment.IsInternal).HasColumnName("is_internal");
            entity.Property(comment => comment.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Ticket>().WithMany().HasForeignKey(comment => comment.TicketId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowState>(entity =>
        {
            entity.ToTable("workflow_states");
            entity.HasKey(state => state.Id);
            entity.HasIndex(state => state.ClientId).HasDatabaseName("ix_workflow_states_client_id");

            entity.Property(state => state.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(state => state.ClientId).HasColumnName("client_id");
            entity.Property(state => state.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(state => state.Color).HasColumnName("color").HasMaxLength(7);
            entity.Property(state => state.IsInitial).HasColumnName("is_initial");
            entity.Property(state => state.IsFinal).HasColumnName("is_final");
            entity.Property(state => state.SortOrder).HasColumnName("sort_order");
            entity.Property(state => state.PausesSla).HasColumnName("pauses_sla").HasDefaultValue(false);
            entity.Property(state => state.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(state => state.ClientId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowTransition>(entity =>
        {
            entity.ToTable("workflow_transitions");
            entity.HasKey(transition => transition.Id);
            entity.HasIndex(transition => transition.ClientId).HasDatabaseName("ix_workflow_transitions_client_id");
            entity.HasIndex(transition => transition.FromStateId).HasDatabaseName("ix_workflow_transitions_from");

            entity.Property(transition => transition.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(transition => transition.ClientId).HasColumnName("client_id");
            entity.Property(transition => transition.FromStateId).HasColumnName("from_state_id");
            entity.Property(transition => transition.ToStateId).HasColumnName("to_state_id");
            entity.Property(transition => transition.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(transition => transition.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(transition => transition.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowState>().WithMany().HasForeignKey(transition => transition.FromStateId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowState>().WithMany().HasForeignKey(transition => transition.ToStateId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("departments");
            entity.HasKey(dept => dept.Id);
            entity.HasIndex(dept => dept.ClientId).HasDatabaseName("ix_departments_client_id");
            entity.HasIndex(dept => dept.IsActive).HasDatabaseName("ix_departments_is_active");

            entity.Property(dept => dept.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(dept => dept.ClientId).HasColumnName("client_id");
            entity.Property(dept => dept.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(dept => dept.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(dept => dept.InheritFromGlobalId).HasColumnName("inherit_from_global_id");
            entity.Property(dept => dept.SortOrder).HasColumnName("sort_order");
            entity.Property(dept => dept.IsActive).HasColumnName("is_active");
            entity.Property(dept => dept.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(dept => dept.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(dept => dept.ClientId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowProfile>(entity =>
        {
            entity.ToTable("workflow_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.DepartmentId).HasDatabaseName("ix_workflow_profiles_dept");
            entity.HasIndex(profile => profile.ClientId).HasDatabaseName("ix_workflow_profiles_client");

            entity.Property(profile => profile.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(profile => profile.ClientId).HasColumnName("client_id");
            entity.Property(profile => profile.DepartmentId).HasColumnName("department_id");
            entity.Property(profile => profile.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(profile => profile.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(profile => profile.SlaHours).HasColumnName("sla_hours");
            entity.Property(profile => profile.FirstResponseSlaHours).HasColumnName("first_response_sla_hours").HasDefaultValue(4);
            entity.Property(profile => profile.DefaultPriority).HasColumnName("default_priority").HasConversion<string>().HasMaxLength(50);
            entity.Property(profile => profile.IsActive).HasColumnName("is_active");
            entity.Property(profile => profile.SlaCalendarId).HasColumnName("sla_calendar_id");
            entity.Property(profile => profile.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(profile => profile.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(profile => profile.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Department>().WithMany().HasForeignKey(profile => profile.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TicketActivityLog>(entity =>
        {
            entity.ToTable("ticket_activity_logs");
            entity.HasKey(log => log.Id);
            entity.HasIndex(log => log.TicketId).HasDatabaseName("ix_ticket_activity_logs_ticket");
            entity.HasIndex(log => log.CreatedAt).HasDatabaseName("ix_ticket_activity_logs_created").IsDescending();
            entity.HasIndex(log => log.Type).HasDatabaseName("ix_ticket_activity_logs_type");

            entity.Property(log => log.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(log => log.TicketId).HasColumnName("ticket_id");
            entity.Property(log => log.Type).HasColumnName("activity_type").HasConversion<string>().HasMaxLength(50);
            entity.Property(log => log.ChangedByUserId).HasColumnName("changed_by_user_id");
            entity.Property(log => log.OldValue).HasColumnName("old_value").HasMaxLength(1000);
            entity.Property(log => log.NewValue).HasColumnName("new_value").HasMaxLength(1000);
            entity.Property(log => log.Comment).HasColumnName("comment").HasMaxLength(2000);
            entity.Property(log => log.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Ticket>().WithMany().HasForeignKey(log => log.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketSavedView>(entity =>
        {
            entity.ToTable("ticket_saved_views");
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => v.UserId).HasDatabaseName("ix_ticket_saved_views_user_id");
            entity.HasIndex(v => v.IsShared).HasDatabaseName("ix_ticket_saved_views_is_shared");

            entity.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(v => v.UserId).HasColumnName("user_id");
            entity.Property(v => v.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(v => v.FilterJson).HasColumnName("filter_json").HasColumnType("jsonb");
            entity.Property(v => v.IsShared).HasColumnName("is_shared");
            entity.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });
    }
}
