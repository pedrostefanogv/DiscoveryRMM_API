using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260314_063)]
public class M063_CreateAutomationScripts : Migration
{
    public override void Up()
    {
        Create.Table("automation_script_definitions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("summary").AsString(2000).NotNullable()
            .WithColumn("script_type").AsInt32().NotNullable()
            .WithColumn("version").AsString(50).NotNullable().WithDefaultValue("1.0.0")
            .WithColumn("execution_frequency").AsString(100).NotNullable().WithDefaultValue("manual")
            .WithColumn("trigger_modes_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("parameters_schema_json").AsCustom("jsonb").Nullable()
            .WithColumn("metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("last_updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_automation_scripts_client_active_updated")
            .OnTable("automation_script_definitions")
            .OnColumn("client_id").Ascending()
            .OnColumn("is_active").Ascending()
            .OnColumn("updated_at").Descending();

        Create.Index("ix_automation_scripts_name_version")
            .OnTable("automation_script_definitions")
            .OnColumn("name").Ascending()
            .OnColumn("version").Ascending();

        Create.Table("automation_script_audits")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("script_id").AsGuid().NotNullable().ForeignKey("automation_script_definitions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("change_type").AsInt32().NotNullable()
            .WithColumn("reason").AsString(2000).Nullable()
            .WithColumn("old_value_json").AsCustom("jsonb").Nullable()
            .WithColumn("new_value_json").AsCustom("jsonb").Nullable()
            .WithColumn("changed_by").AsString(256).Nullable()
            .WithColumn("ip_address").AsString(64).Nullable()
            .WithColumn("correlation_id").AsString(64).Nullable()
            .WithColumn("changed_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_automation_script_audits_script_changed")
            .OnTable("automation_script_audits")
            .OnColumn("script_id").Ascending()
            .OnColumn("changed_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("automation_script_audits");
        Delete.Table("automation_script_definitions");
    }
}
