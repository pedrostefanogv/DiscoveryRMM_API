using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260415_095)]
public class M095_CreateAgentAlertDefinitions : Migration
{
    public override void Up()
    {
        Create.Table("agent_alert_definitions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("message").AsString(2000).NotNullable()
            .WithColumn("alert_type").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("timeout_seconds").AsInt32().Nullable()
            .WithColumn("actions_json").AsCustom("jsonb").Nullable()
            .WithColumn("default_action").AsString(100).Nullable()
            .WithColumn("icon").AsString(50).NotNullable().WithDefaultValue("info")
            .WithColumn("scope_type").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("scope_agent_id").AsGuid().Nullable()
            .WithColumn("scope_site_id").AsGuid().Nullable()
            .WithColumn("scope_client_id").AsGuid().Nullable()
            .WithColumn("scope_label_name").AsString(120).Nullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("scheduled_at").AsCustom("timestamptz").Nullable()
            .WithColumn("expires_at").AsCustom("timestamptz").Nullable()
            .WithColumn("dispatched_at").AsCustom("timestamptz").Nullable()
            .WithColumn("dispatched_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("ticket_id").AsGuid().Nullable()
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agent_alerts_status")
            .OnTable("agent_alert_definitions")
            .OnColumn("status").Ascending();

        Create.Index("ix_agent_alerts_status_scheduled_at")
            .OnTable("agent_alert_definitions")
            .OnColumn("status").Ascending()
            .OnColumn("scheduled_at").Ascending();

        Create.Index("ix_agent_alerts_scope_client")
            .OnTable("agent_alert_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("scope_client_id").Ascending();

        Create.Index("ix_agent_alerts_scope_site")
            .OnTable("agent_alert_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("scope_site_id").Ascending();

        Create.Index("ix_agent_alerts_scope_agent")
            .OnTable("agent_alert_definitions")
            .OnColumn("scope_agent_id").Ascending();

        Create.Index("ix_agent_alerts_ticket_id")
            .OnTable("agent_alert_definitions")
            .OnColumn("ticket_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("agent_alert_definitions");
    }
}
