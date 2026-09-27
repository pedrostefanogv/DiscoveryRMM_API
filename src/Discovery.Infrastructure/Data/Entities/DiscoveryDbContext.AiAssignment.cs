using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// Triagem por IA na auto-atribuição de chamados: métricas por atendente,
// decisões auditáveis e fila assíncrona.
public partial class DiscoveryDbContext
{
    static partial void ConfigureAiAssignment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TechnicianMetricsSnapshot>(entity =>
        {
            entity.ToTable("technician_metrics_snapshots");
            entity.HasKey(snapshot => snapshot.Id);
            entity.HasIndex(snapshot => snapshot.UserId).IsUnique().HasDatabaseName("ux_technician_metrics_snapshots_user");

            entity.Property(snapshot => snapshot.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(snapshot => snapshot.UserId).HasColumnName("user_id");
            entity.Property(snapshot => snapshot.WindowDays).HasColumnName("window_days");
            entity.Property(snapshot => snapshot.ComputedAt).HasColumnName("computed_at").HasColumnType("timestamptz");
            entity.Property(snapshot => snapshot.AssignedTotal).HasColumnName("assigned_total");
            entity.Property(snapshot => snapshot.ResolvedTotal).HasColumnName("resolved_total");
            entity.Property(snapshot => snapshot.OpenNow).HasColumnName("open_now");
            entity.Property(snapshot => snapshot.AvgFirstResponseMinutes).HasColumnName("avg_first_response_minutes").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.AvgResolutionMinutes).HasColumnName("avg_resolution_minutes").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.P90ResolutionMinutes).HasColumnName("p90_resolution_minutes").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.SlaBreachRate).HasColumnName("sla_breach_rate").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.ReopenRate).HasColumnName("reopen_rate").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.CsatAverage).HasColumnName("csat_average").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.CsatRatedCount).HasColumnName("csat_rated_count");
            entity.Property(snapshot => snapshot.DifficultyAverage).HasColumnName("difficulty_average").HasColumnType("double precision");
            entity.Property(snapshot => snapshot.TopCategoriesJson).HasColumnName("top_categories_json").HasColumnType("jsonb");
            entity.Property(snapshot => snapshot.TopTagsJson).HasColumnName("top_tags_json").HasColumnType("jsonb");

            entity.HasOne<User>().WithMany().HasForeignKey(snapshot => snapshot.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketAssignmentDecision>(entity =>
        {
            entity.ToTable("ticket_assignment_decisions");
            entity.HasKey(decision => decision.Id);
            entity.HasIndex(decision => decision.TicketId).HasDatabaseName("ix_ticket_assignment_decisions_ticket");
            entity.HasIndex(decision => new { decision.DepartmentId, decision.CreatedAt })
                .HasDatabaseName("ix_ticket_assignment_decisions_department_created");

            entity.Property(decision => decision.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(decision => decision.TicketId).HasColumnName("ticket_id");
            entity.Property(decision => decision.DepartmentId).HasColumnName("department_id");
            entity.Property(decision => decision.Mode).HasColumnName("mode");
            entity.Property(decision => decision.StrategySource).HasColumnName("strategy_source").HasMaxLength(40);
            entity.Property(decision => decision.Difficulty).HasColumnName("difficulty");
            entity.Property(decision => decision.ChosenUserId).HasColumnName("chosen_user_id");
            entity.Property(decision => decision.Confidence).HasColumnName("confidence").HasColumnType("double precision");
            entity.Property(decision => decision.Score).HasColumnName("score").HasColumnType("double precision");
            entity.Property(decision => decision.CandidatesJson).HasColumnName("candidates_json").HasColumnType("jsonb");
            entity.Property(decision => decision.Rationale).HasColumnName("rationale").HasColumnType("text");
            entity.Property(decision => decision.Model).HasColumnName("model").HasMaxLength(200);
            entity.Property(decision => decision.TokensUsed).HasColumnName("tokens_used");
            entity.Property(decision => decision.MaxOutputTokens).HasColumnName("max_output_tokens");
            entity.Property(decision => decision.PromptChars).HasColumnName("prompt_chars");
            entity.Property(decision => decision.Applied).HasColumnName("applied");
            entity.Property(decision => decision.NotAppliedReason).HasColumnName("not_applied_reason").HasColumnType("text");
            entity.Property(decision => decision.OverriddenAt).HasColumnName("overridden_at").HasColumnType("timestamptz");
            entity.Property(decision => decision.OverriddenByUserId).HasColumnName("overridden_by_user_id");
            entity.Property(decision => decision.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne<Ticket>().WithMany().HasForeignKey(decision => decision.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiAssignmentQueueItem>(entity =>
        {
            entity.ToTable("ai_assignment_queue");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.TicketId).IsUnique().HasDatabaseName("ux_ai_assignment_queue_ticket");
            entity.HasIndex(item => new { item.Status, item.AvailableAt })
                .HasDatabaseName("ix_ai_assignment_queue_status_available");
            entity.HasIndex(item => item.UpdatedAt).HasDatabaseName("ix_ai_assignment_queue_updated_at");

            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.TicketId).HasColumnName("ticket_id");
            entity.Property(item => item.DepartmentId).HasColumnName("department_id");
            entity.Property(item => item.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(item => item.Attempts).HasColumnName("attempts");
            entity.Property(item => item.AvailableAt).HasColumnName("available_at").HasColumnType("timestamptz");
            entity.Property(item => item.LastError).HasColumnName("last_error").HasColumnType("text");
            entity.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(80);
            entity.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Ticket>().WithMany().HasForeignKey(item => item.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TechnicianSkillSuggestion>(entity =>
        {
            entity.ToTable("technician_skill_suggestions");
            entity.HasKey(suggestion => suggestion.Id);
            entity.HasIndex(suggestion => new { suggestion.DepartmentId, suggestion.CreatedAt })
                .HasDatabaseName("ix_technician_skill_suggestions_department_created");
            entity.HasIndex(suggestion => new { suggestion.DepartmentId, suggestion.UserId })
                .HasDatabaseName("ix_technician_skill_suggestions_department_user");

            entity.Property(suggestion => suggestion.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(suggestion => suggestion.DepartmentId).HasColumnName("department_id");
            entity.Property(suggestion => suggestion.UserId).HasColumnName("user_id");
            entity.Property(suggestion => suggestion.WindowDays).HasColumnName("window_days");
            entity.Property(suggestion => suggestion.SuggestedTagsJson).HasColumnName("suggested_tags_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.AppliedTagsJson).HasColumnName("applied_tags_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(suggestion => suggestion.AutoApplied).HasColumnName("auto_applied");
            entity.Property(suggestion => suggestion.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.DecidedByUserId).HasColumnName("decided_by_user_id");

            entity.HasOne<Department>().WithMany().HasForeignKey(suggestion => suggestion.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(suggestion => suggestion.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiWeightSuggestion>(entity =>
        {
            entity.ToTable("ai_weight_suggestions");
            entity.HasKey(suggestion => suggestion.Id);
            entity.HasIndex(suggestion => new { suggestion.DepartmentId, suggestion.CreatedAt })
                .HasDatabaseName("ix_ai_weight_suggestions_department_created");

            entity.Property(suggestion => suggestion.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(suggestion => suggestion.DepartmentId).HasColumnName("department_id");
            entity.Property(suggestion => suggestion.CycleDays).HasColumnName("cycle_days");
            entity.Property(suggestion => suggestion.WindowStart).HasColumnName("window_start").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.WindowEnd).HasColumnName("window_end").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.CurrentWeightsJson).HasColumnName("current_weights_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.SuggestedWeightsJson).HasColumnName("suggested_weights_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            entity.Property(suggestion => suggestion.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(suggestion => suggestion.AutoApplied).HasColumnName("auto_applied");
            entity.Property(suggestion => suggestion.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(suggestion => suggestion.DecidedByUserId).HasColumnName("decided_by_user_id");

            entity.HasOne<Department>().WithMany().HasForeignKey(suggestion => suggestion.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
