using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_107)]
public class M107_CreateAutoTicketCore : Migration
{
    public override void Up()
    {
        Create.Table("auto_ticket_rules")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("priority_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("scope_level").AsInt32().NotNullable()
            .WithColumn("scope_id").AsGuid().Nullable()
            .WithColumn("alert_code_filter").AsString(200).Nullable()
            .WithColumn("source_filter").AsInt32().Nullable()
            .WithColumn("severity_min").AsInt32().Nullable()
            .WithColumn("severity_max").AsInt32().Nullable()
            .WithColumn("match_labels_any_json").AsCustom("jsonb").Nullable()
            .WithColumn("match_labels_all_json").AsCustom("jsonb").Nullable()
            .WithColumn("exclude_labels_json").AsCustom("jsonb").Nullable()
            .WithColumn("payload_predicate_json").AsCustom("jsonb").Nullable()
            .WithColumn("action").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("target_department_id").AsGuid().Nullable()
            .WithColumn("target_workflow_profile_id").AsGuid().Nullable()
            .WithColumn("target_category").AsString(100).Nullable()
            .WithColumn("target_priority").AsInt32().Nullable()
            .WithColumn("dedup_window_minutes").AsInt32().NotNullable().WithDefaultValue(60)
            .WithColumn("cooldown_minutes").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_auto_ticket_rules_scope_enabled")
            .OnTable("auto_ticket_rules")
            .OnColumn("scope_level").Ascending()
            .OnColumn("is_enabled").Ascending();

        Create.Index("ix_auto_ticket_rules_alert_code")
            .OnTable("auto_ticket_rules")
            .OnColumn("alert_code_filter").Ascending();

        Create.Table("alert_correlation_locks")
            .WithColumn("dedup_key").AsString(500).PrimaryKey()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("last_ticket_id").AsGuid().Nullable()
            .WithColumn("last_alert_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
    }

    public override void Down()
    {
        Delete.Table("alert_correlation_locks");
        Delete.Table("auto_ticket_rules");
    }
}