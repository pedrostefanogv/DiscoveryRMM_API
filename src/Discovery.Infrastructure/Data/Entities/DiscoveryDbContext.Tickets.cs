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
            entity.Property(ticket => ticket.SubmissionSnapshotMarkdown).HasColumnName("submission_snapshot_md");
            entity.Property(ticket => ticket.TemplateId).HasColumnName("template_id");
            entity.Property(ticket => ticket.TemplateName).HasColumnName("template_name").HasMaxLength(200);
            entity.HasIndex(ticket => ticket.TemplateId).HasDatabaseName("ix_tickets_template_id");
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
            entity.Property(ticket => ticket.RatingFeedback).HasColumnName("rating_feedback").HasMaxLength(2000);
            entity.Property(ticket => ticket.RatedAt).HasColumnName("rated_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.RatedBy).HasColumnName("rated_by").HasMaxLength(255);
            entity.Property(ticket => ticket.Category).HasColumnName("category").HasMaxLength(100);
            entity.Property(ticket => ticket.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(ticket => ticket.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            // Concorrência otimista: xmin é a versão de linha do Postgres.
            entity.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

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
            entity.Property(dept => dept.AssignmentStrategy).HasColumnName("assignment_strategy");
            entity.Property(dept => dept.RoundRobinLastUserId).HasColumnName("round_robin_last_user_id");
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

        modelBuilder.Entity<TicketRelation>(entity =>
        {
            entity.ToTable("ticket_relations");
            entity.HasKey(relation => relation.Id);
            entity.HasIndex(relation => relation.SourceTicketId).HasDatabaseName("ix_ticket_relations_source");
            entity.HasIndex(relation => relation.TargetTicketId).HasDatabaseName("ix_ticket_relations_target");

            entity.Property(relation => relation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(relation => relation.SourceTicketId).HasColumnName("source_ticket_id");
            entity.Property(relation => relation.TargetTicketId).HasColumnName("target_ticket_id");
            entity.Property(relation => relation.RelationTypeValue).HasColumnName("relation_type");
            entity.Property(relation => relation.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(relation => relation.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            // FKs para tickets: cascade garante que deletar um chamado limpe as relações.
            entity.HasOne(relation => relation.SourceTicket).WithMany()
                .HasForeignKey(relation => relation.SourceTicketId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(relation => relation.TargetTicket).WithMany()
                .HasForeignKey(relation => relation.TargetTicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketMergeRecord>(entity =>
        {
            entity.ToTable("ticket_merge_records");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => record.SourceTicketId).HasDatabaseName("ix_ticket_merge_records_source");
            entity.HasIndex(record => record.TargetTicketId).HasDatabaseName("ix_ticket_merge_records_target");

            entity.Property(record => record.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(record => record.SourceTicketId).HasColumnName("source_ticket_id");
            entity.Property(record => record.TargetTicketId).HasColumnName("target_ticket_id");
            entity.Property(record => record.MergedBy).HasColumnName("merged_by").HasMaxLength(255);
            entity.Property(record => record.Reason).HasColumnName("reason").HasMaxLength(1000);
            entity.Property(record => record.MergedAt).HasColumnName("merged_at").HasColumnType("timestamptz");

            entity.HasOne(record => record.SourceTicket).WithMany()
                .HasForeignKey(record => record.SourceTicketId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(record => record.TargetTicket).WithMany()
                .HasForeignKey(record => record.TargetTicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketMacro>(entity =>
        {
            entity.ToTable("ticket_macros");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.ClientId).HasDatabaseName("ix_ticket_macros_client");
            entity.HasIndex(m => m.DepartmentId).HasDatabaseName("ix_ticket_macros_department");
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.ClientId).HasColumnName("client_id");
            entity.Property(m => m.DepartmentId).HasColumnName("department_id");
            entity.Property(m => m.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(m => m.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(m => m.Content).HasColumnName("content");
            entity.Property(m => m.IsActive).HasColumnName("is_active");
            entity.Property(m => m.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(m => m.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TicketAnswer>(entity =>
        {
            entity.ToTable("ticket_answers");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.TicketId, a.QuestionKey })
                .IsUnique()
                .HasDatabaseName("ux_ticket_answers_ticket_question");
            entity.HasIndex(a => new { a.QuestionKey, a.ValueText }).HasDatabaseName("ix_ticket_answers_key_value");
            entity.HasIndex(a => a.TemplateId).HasDatabaseName("ix_ticket_answers_template");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.TicketId).HasColumnName("ticket_id");
            entity.Property(a => a.TemplateId).HasColumnName("template_id");
            entity.Property(a => a.QuestionKey).HasColumnName("question_key").HasMaxLength(100);
            entity.Property(a => a.QuestionLabel).HasColumnName("question_label").HasMaxLength(200);
            entity.Property(a => a.ValueText).HasColumnName("value_text");
            entity.Property(a => a.ValueJson).HasColumnName("value_json").HasColumnType("jsonb");
            entity.Property(a => a.SortOrder).HasColumnName("sort_order");
            entity.Property(a => a.Embedding).HasColumnName("embedding").HasColumnType("vector(1536)");
            entity.Property(a => a.EmbeddingGeneratedAt).HasColumnName("embedding_generated_at").HasColumnType("timestamptz");
            entity.Property(a => a.IsSensitive).HasColumnName("is_sensitive");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketTemplate>(entity =>
        {
            entity.ToTable("ticket_templates");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.ClientId).HasDatabaseName("ix_ticket_templates_client");
            entity.HasIndex(t => t.DepartmentId).HasDatabaseName("ix_ticket_templates_department");
            entity.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(t => t.ClientId).HasColumnName("client_id");
            entity.Property(t => t.DepartmentId).HasColumnName("department_id");
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(t => t.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(t => t.Description).HasColumnName("description");
            entity.Property(t => t.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(50);
            entity.Property(t => t.Category).HasColumnName("category").HasMaxLength(100);
            entity.Property(t => t.CustomFieldDefaultsJson).HasColumnName("custom_field_defaults_json").HasColumnType("jsonb");
            entity.Property(t => t.QuestionsJson).HasColumnName("questions_json").HasColumnType("jsonb");
            entity.Property(t => t.IsActive).HasColumnName("is_active");
            entity.Property(t => t.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(t => t.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<DepartmentMember>(entity =>
        {
            entity.ToTable("department_members");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.DepartmentId).HasDatabaseName("ix_department_members_department");
            entity.HasIndex(m => new { m.DepartmentId, m.UserId }).IsUnique().HasDatabaseName("ux_department_members_dept_user");
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.DepartmentId).HasColumnName("department_id");
            entity.Property(m => m.UserId).HasColumnName("user_id");
            entity.Property(m => m.IsActive).HasColumnName("is_active");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasOne<Department>().WithMany().HasForeignKey(m => m.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
