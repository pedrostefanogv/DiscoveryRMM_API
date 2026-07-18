using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260717_060)]
public class M060_AddAiChatFeedbackFields : Migration
{
    public override void Up()
    {
        // Adiciona campos de feedback à tabela ai_chat_messages
        if (!Schema.Table("ai_chat_messages").Column("feedback_score").Exists())
        {
            Alter.Table("ai_chat_messages")
                .AddColumn("feedback_score").AsInt32().Nullable();
        }

        if (!Schema.Table("ai_chat_messages").Column("feedback_comment").Exists())
        {
            Alter.Table("ai_chat_messages")
                .AddColumn("feedback_comment").AsString(500).Nullable();
        }

        // Adiciona campo de feedback_score às sessões para nota geral da conversa
        if (!Schema.Table("ai_chat_sessions").Column("feedback_score").Exists())
        {
            Alter.Table("ai_chat_sessions")
                .AddColumn("feedback_score").AsInt32().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("ai_chat_messages").Column("feedback_score").Exists())
        {
            Delete.Column("feedback_score").FromTable("ai_chat_messages");
        }
        if (Schema.Table("ai_chat_messages").Column("feedback_comment").Exists())
        {
            Delete.Column("feedback_comment").FromTable("ai_chat_messages");
        }
        if (Schema.Table("ai_chat_sessions").Column("feedback_score").Exists())
        {
            Delete.Column("feedback_score").FromTable("ai_chat_sessions");
        }
    }
}
