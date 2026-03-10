using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260310_044)]
public class M044_CreateAiChatTables : Migration
{
    public override void Up()
    {
        // ai_chat_sessions
        Create.Table("ai_chat_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("site_id").AsGuid().NotNullable().ForeignKey("sites", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("client_id").AsGuid().NotNullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("topic").AsString(100).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("closed_at").AsCustom("timestamptz").Nullable()
            .WithColumn("created_by_ip").AsString(45).NotNullable()
            .WithColumn("trace_id").AsString(100).Nullable()
            .WithColumn("expires_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("deleted_at").AsCustom("timestamptz").Nullable();
            
        Create.Index("ix_ai_chat_sessions_agent_created")
            .OnTable("ai_chat_sessions")
            .OnColumn("agent_id").Ascending()
            .OnColumn("created_at").Descending();
            
        Create.Index("ix_ai_chat_sessions_expires")
            .OnTable("ai_chat_sessions")
            .OnColumn("expires_at").Ascending();
            
        Create.Index("ix_ai_chat_sessions_deleted")
            .OnTable("ai_chat_sessions")
            .OnColumn("deleted_at").Ascending();

        // ai_chat_messages
        Create.Table("ai_chat_messages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("session_id").AsGuid().NotNullable().ForeignKey("ai_chat_sessions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("sequence_number").AsInt32().NotNullable()
            .WithColumn("role").AsString(20).NotNullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("tokens_used").AsInt32().Nullable()
            .WithColumn("latency_ms").AsInt32().Nullable()
            .WithColumn("model_version").AsString(50).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("tool_name").AsString(100).Nullable()
            .WithColumn("tool_call_id").AsString(100).Nullable()
            .WithColumn("tool_arguments_json").AsString(int.MaxValue).Nullable()
            .WithColumn("trace_id").AsString(100).Nullable();
            
        Create.Index("ix_ai_chat_messages_session_sequence")
            .OnTable("ai_chat_messages")
            .OnColumn("session_id").Ascending()
            .OnColumn("sequence_number").Ascending();

        // ai_chat_jobs
        Create.Table("ai_chat_jobs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("session_id").AsGuid().NotNullable().ForeignKey("ai_chat_sessions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("user_message").AsString(int.MaxValue).NotNullable()
            .WithColumn("assistant_message").AsString(int.MaxValue).Nullable()
            .WithColumn("tokens_used").AsInt32().Nullable()
            .WithColumn("error_message").AsString(1000).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("started_at").AsCustom("timestamptz").Nullable()
            .WithColumn("completed_at").AsCustom("timestamptz").Nullable()
            .WithColumn("trace_id").AsString(100).Nullable();
            
        Create.Index("ix_ai_chat_jobs_agent_status")
            .OnTable("ai_chat_jobs")
            .OnColumn("agent_id").Ascending()
            .OnColumn("status").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("ai_chat_jobs");
        Delete.Table("ai_chat_messages");
        Delete.Table("ai_chat_sessions");
    }
}
