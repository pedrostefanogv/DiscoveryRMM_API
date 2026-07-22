using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260722_137)]
public class M137_AddToolCallsJsonToAiChatMessages : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ai_chat_messages").Column("tool_calls_json").Exists())
        {
            Alter.Table("ai_chat_messages")
                .AddColumn("tool_calls_json").AsString(int.MaxValue).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("ai_chat_messages").Column("tool_calls_json").Exists())
        {
            Delete.Column("tool_calls_json").FromTable("ai_chat_messages");
        }
    }
}
