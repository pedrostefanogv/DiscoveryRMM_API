using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── AI Chat & MCP: AiChatSession, AiChatMessage, AiChatJob, McpToolPolicy ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureAiChat(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiChatSession>(entity =>
        {
            entity.ToTable("ai_chat_sessions");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.SiteId).HasColumnName("site_id");
            entity.Property(s => s.ClientId).HasColumnName("client_id");
            entity.Property(s => s.Topic).HasColumnName("topic").HasMaxLength(100);
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(s => s.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(45);
            entity.Property(s => s.TraceId).HasColumnName("trace_id").HasMaxLength(100);
            entity.Property(s => s.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(s => s.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            entity.HasMany(s => s.Messages).WithOne(m => m.Session).HasForeignKey(m => m.SessionId);
        });

        modelBuilder.Entity<AiChatMessage>(entity =>
        {
            entity.ToTable("ai_chat_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.SessionId).HasColumnName("session_id");
            entity.Property(m => m.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(m => m.Role).HasColumnName("role").HasMaxLength(20);
            entity.Property(m => m.Content).HasColumnName("content");
            entity.Property(m => m.TokensUsed).HasColumnName("tokens_used");
            entity.Property(m => m.LatencyMs).HasColumnName("latency_ms");
            entity.Property(m => m.ModelVersion).HasColumnName("model_version").HasMaxLength(50);
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(m => m.ToolName).HasColumnName("tool_name").HasMaxLength(100);
            entity.Property(m => m.ToolCallId).HasColumnName("tool_call_id").HasMaxLength(100);
            entity.Property(m => m.ToolArgumentsJson).HasColumnName("tool_arguments_json");
            entity.Property(m => m.TraceId).HasColumnName("trace_id").HasMaxLength(100);
        });

        modelBuilder.Entity<AiChatJob>(entity =>
        {
            entity.ToTable("ai_chat_jobs");
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(j => j.SessionId).HasColumnName("session_id");
            entity.Property(j => j.AgentId).HasColumnName("agent_id");
            entity.Property(j => j.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(j => j.UserMessage).HasColumnName("user_message");
            entity.Property(j => j.AssistantMessage).HasColumnName("assistant_message");
            entity.Property(j => j.TokensUsed).HasColumnName("tokens_used");
            entity.Property(j => j.ErrorMessage).HasColumnName("error_message").HasMaxLength(1000);
            entity.Property(j => j.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(j => j.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(j => j.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            entity.Property(j => j.TraceId).HasColumnName("trace_id").HasMaxLength(100);
        });
    }
}
