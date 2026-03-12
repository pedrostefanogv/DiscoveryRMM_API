using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260312_057)]
public class M057_CreateAgentLabeling : Migration
{
    public override void Up()
    {
        Create.Table("agent_label_rules")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("apply_mode").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("expression_json").AsCustom("jsonb").NotNullable()
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agent_label_rules_is_enabled")
            .OnTable("agent_label_rules")
            .OnColumn("is_enabled").Ascending();

        Create.Index("ix_agent_label_rules_label")
            .OnTable("agent_label_rules")
            .OnColumn("label").Ascending();

        Create.Table("agent_labels")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("source_type").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_agent_labels_agent_label")
            .OnTable("agent_labels")
            .OnColumn("agent_id").Ascending()
            .OnColumn("label").Ascending()
            .WithOptions().Unique();

        Create.Table("agent_label_rule_matches")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("rule_id").AsGuid().NotNullable().ForeignKey("agent_label_rules", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("matched_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_evaluated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_agent_label_rule_matches_rule_agent")
            .OnTable("agent_label_rule_matches")
            .OnColumn("rule_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_agent_label_rule_matches_agent_label")
            .OnTable("agent_label_rule_matches")
            .OnColumn("agent_id").Ascending()
            .OnColumn("label").Ascending();
    }

    public override void Down()
    {
        Delete.Table("agent_label_rule_matches");
        Delete.Table("agent_labels");
        Delete.Table("agent_label_rules");
    }
}
