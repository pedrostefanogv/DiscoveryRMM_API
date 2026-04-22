using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260314_064)]
public class M064_CreateAutomationTasks : Migration
{
    public override void Up()
    {
        Create.Table("automation_task_definitions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("action_type").AsInt32().NotNullable()
            .WithColumn("installation_type").AsInt32().Nullable()
            .WithColumn("package_id").AsString(300).Nullable()
            .WithColumn("script_id").AsGuid().Nullable().ForeignKey("automation_script_definitions", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("command_payload").AsCustom("text").Nullable()
            .WithColumn("scope_type").AsInt32().NotNullable()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("sites", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("include_tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("exclude_tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("trigger_immediate").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("trigger_recurring").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("trigger_on_user_login").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("trigger_on_agent_check_in").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("schedule_cron").AsString(100).Nullable()
            .WithColumn("requires_approval").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("last_updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_automation_tasks_scope")
            .OnTable("automation_task_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending();

        Create.Index("ix_automation_tasks_active_updated")
            .OnTable("automation_task_definitions")
            .OnColumn("is_active").Ascending()
            .OnColumn("updated_at").Descending();

        Create.Table("automation_task_audits")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("task_id").AsGuid().NotNullable().ForeignKey("automation_task_definitions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("change_type").AsInt32().NotNullable()
            .WithColumn("reason").AsString(2000).Nullable()
            .WithColumn("old_value_json").AsCustom("jsonb").Nullable()
            .WithColumn("new_value_json").AsCustom("jsonb").Nullable()
            .WithColumn("changed_by").AsString(256).Nullable()
            .WithColumn("ip_address").AsString(64).Nullable()
            .WithColumn("correlation_id").AsString(64).Nullable()
            .WithColumn("changed_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_automation_task_audits_task_changed")
            .OnTable("automation_task_audits")
            .OnColumn("task_id").Ascending()
            .OnColumn("changed_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("automation_task_audits");
        Delete.Table("automation_task_definitions");
    }
}
