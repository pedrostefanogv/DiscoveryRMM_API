using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260314_065)]
public class M065_CreateAutomationExecutionReports : Migration
{
    public override void Up()
    {
        Create.Table("automation_execution_reports")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("command_id").AsGuid().NotNullable().ForeignKey("agent_commands", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("task_id").AsGuid().Nullable().ForeignKey("automation_task_definitions", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("script_id").AsGuid().Nullable().ForeignKey("automation_script_definitions", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("source_type").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("correlation_id").AsString(64).Nullable()
            .WithColumn("request_metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("ack_metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("result_metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("acknowledged_at").AsCustom("timestamptz").Nullable()
            .WithColumn("result_received_at").AsCustom("timestamptz").Nullable()
            .WithColumn("exit_code").AsInt32().Nullable()
            .WithColumn("error_message").AsString(4000).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_automation_execution_reports_command")
            .OnTable("automation_execution_reports")
            .OnColumn("command_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_automation_execution_reports_agent_created")
            .OnTable("automation_execution_reports")
            .OnColumn("agent_id").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("automation_execution_reports");
    }
}
