using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_005)]
public class M005_CreateAgentCommands : Migration
{
    public override void Up()
    {
        Create.Table("agent_commands")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_commands_agent", "agents", "id")
            .WithColumn("command_type").AsInt32().NotNullable()
            .WithColumn("payload").AsString(int.MaxValue).NotNullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0) // Pending
            .WithColumn("result").AsString(int.MaxValue).Nullable()
            .WithColumn("exit_code").AsInt32().Nullable()
            .WithColumn("error_message").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("sent_at").AsCustom("timestamptz").Nullable()
            .WithColumn("completed_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_commands_agent_id").OnTable("agent_commands").OnColumn("agent_id");
        Create.Index("ix_commands_status").OnTable("agent_commands").OnColumn("status");
    }

    public override void Down()
    {
        Delete.Table("agent_commands");
    }
}
