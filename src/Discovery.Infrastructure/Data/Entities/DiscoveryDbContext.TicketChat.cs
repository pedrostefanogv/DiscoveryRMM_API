using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Ticket Support Chat: TicketChatSession, TicketChatMessage, TicketChatMessageAttachment ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureTicketChat(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TicketChatSession>(entity =>
        {
            entity.ToTable("ticket_chat_sessions");
            entity.HasKey(s => s.Id);

            entity.HasIndex(s => s.TicketId).HasDatabaseName("ix_ticket_chat_sessions_ticket_id");
            entity.HasIndex(s => new { s.AgentId, s.Status }).HasDatabaseName("ix_ticket_chat_sessions_agent_id_status");
            entity.HasIndex(s => new { s.Status, s.InviteExpiresAt }).HasDatabaseName("ix_ticket_chat_sessions_status_invite_expires_at");

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.TicketId).HasColumnName("ticket_id");
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.ClientId).HasColumnName("client_id");
            entity.Property(s => s.SiteId).HasColumnName("site_id");
            entity.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
            entity.Property(s => s.StartedByUserId).HasColumnName("started_by_user_id");
            entity.Property(s => s.StartedByName).HasColumnName("started_by_name").HasMaxLength(255);
            entity.Property(s => s.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(s => s.InviteExpiresAt).HasColumnName("invite_expires_at").HasColumnType("timestamptz");
            entity.Property(s => s.AcceptedAt).HasColumnName("accepted_at").HasColumnType("timestamptz");
            entity.Property(s => s.DeclinedAt).HasColumnName("declined_at").HasColumnType("timestamptz");
            entity.Property(s => s.DeclinedReason).HasColumnName("declined_reason").HasMaxLength(64);
            entity.Property(s => s.EndedAt).HasColumnName("ended_at").HasColumnType("timestamptz");
            entity.Property(s => s.EndedBy).HasColumnName("ended_by").HasConversion<string>().HasMaxLength(16);
            entity.Property(s => s.EndReason).HasColumnName("end_reason").HasMaxLength(64);
            entity.Property(s => s.LastMessageAt).HasColumnName("last_message_at").HasColumnType("timestamptz");
            entity.Property(s => s.MessageCount).HasColumnName("message_count");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            // FK opcional: sessões avulsas (sem chamado) têm ticket_id nulo.
            entity.HasOne(s => s.Ticket).WithMany().HasForeignKey(s => s.TicketId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.Agent).WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(s => s.Messages).WithOne(m => m.Session).HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketChatMessage>(entity =>
        {
            entity.ToTable("ticket_chat_messages");
            entity.HasKey(m => m.Id);

            entity.HasIndex(m => new { m.SessionId, m.CreatedAt }).HasDatabaseName("ix_ticket_chat_messages_session_created_at");
            entity.HasIndex(m => new { m.SessionId, m.SequenceNumber }).IsUnique().HasDatabaseName("ux_ticket_chat_messages_session_seq");
            entity.HasIndex(m => new { m.SessionId, m.ClientMessageId }).IsUnique().HasDatabaseName("ux_ticket_chat_messages_session_client");

            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.SessionId).HasColumnName("session_id");
            entity.Property(m => m.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(m => m.ClientMessageId).HasColumnName("client_message_id");
            entity.Property(m => m.AuthorKind).HasColumnName("author_kind").HasConversion<string>().HasMaxLength(16);
            entity.Property(m => m.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(m => m.AuthorName).HasColumnName("author_name").HasMaxLength(255);
            entity.Property(m => m.Body).HasColumnName("body").HasColumnType("text");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasMany(m => m.Attachments).WithOne(a => a.Message).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketChatMessageAttachment>(entity =>
        {
            entity.ToTable("ticket_chat_message_attachments");
            entity.HasKey(a => a.Id);

            entity.HasIndex(a => a.AttachmentId).HasDatabaseName("ix_ticket_chat_message_attachments_attachment_id");
            entity.HasIndex(a => new { a.MessageId, a.AttachmentId }).IsUnique().HasDatabaseName("ux_ticket_chat_message_attachments_msg_att");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.MessageId).HasColumnName("message_id");
            entity.Property(a => a.AttachmentId).HasColumnName("attachment_id");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });
    }
}
