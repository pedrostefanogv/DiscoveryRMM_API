using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_108)]
public class M108_CreateAgentMonitoringEvents : Migration
{
    public override void Up()
    {
        Create.Table("agent_monitoring_events")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("site_id").AsGuid().Nullable()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("alert_code").AsString(200).NotNullable()
            .WithColumn("severity").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("message").AsString(2000).NotNullable()
            .WithColumn("metric_key").AsString(200).Nullable()
            .WithColumn("metric_value").AsDecimal(18, 4).Nullable()
            .WithColumn("payload_json").AsCustom("jsonb").Nullable()
            .WithColumn("labels_snapshot_json").AsCustom("jsonb").Nullable()
            .WithColumn("source").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("source_ref_id").AsGuid().Nullable()
            .WithColumn("correlation_id").AsString(128).Nullable()
            .WithColumn("occurred_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_monitoring_events_agent_code_occurred")
            .OnTable("agent_monitoring_events")
            .OnColumn("agent_id").Ascending()
            .OnColumn("alert_code").Ascending()
            .OnColumn("occurred_at").Ascending();

        Create.Index("ix_monitoring_events_client_occurred")
            .OnTable("agent_monitoring_events")
            .OnColumn("client_id").Ascending()
            .OnColumn("occurred_at").Ascending();

        Create.Table("auto_ticket_rule_executions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("rule_id").AsGuid().Nullable()
            .WithColumn("monitoring_event_id").AsGuid().NotNullable()
            .WithColumn("agent_id").AsGuid().Nullable()
            .WithColumn("evaluated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("decision").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("reason").AsString(500).Nullable()
            .WithColumn("created_ticket_id").AsGuid().Nullable()
            .WithColumn("dedup_key").AsString(500).Nullable()
            .WithColumn("dedup_hit").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("payload_snapshot_json").AsCustom("jsonb").Nullable();

        Create.Index("ix_auto_ticket_rule_executions_event_id")
            .OnTable("auto_ticket_rule_executions")
            .OnColumn("monitoring_event_id").Ascending();

        Create.Index("ix_auto_ticket_rule_executions_agent_evaluated")
            .OnTable("auto_ticket_rule_executions")
            .OnColumn("agent_id").Ascending()
            .OnColumn("evaluated_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("auto_ticket_rule_executions");
        Delete.Table("agent_monitoring_events");
    }
}