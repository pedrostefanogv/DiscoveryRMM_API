using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260415_094)]
public class M094_CreateCustomFields : Migration
{
    public override void Up()
    {
        Create.Table("custom_field_definitions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("label").AsString(200).NotNullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("scope_type").AsInt32().NotNullable()
            .WithColumn("data_type").AsInt32().NotNullable()
            .WithColumn("is_required").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("is_secret").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("options_json").AsCustom("jsonb").Nullable()
            .WithColumn("validation_regex").AsString(500).Nullable()
            .WithColumn("min_length").AsInt32().Nullable()
            .WithColumn("max_length").AsInt32().Nullable()
            .WithColumn("min_value").AsDecimal(18, 6).Nullable()
            .WithColumn("max_value").AsDecimal(18, 6).Nullable()
            .WithColumn("allow_runtime_read").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("allow_agent_write").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("runtime_access_mode").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_custom_field_definitions_scope_name")
            .OnTable("custom_field_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("name").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_custom_field_definitions_scope_active")
            .OnTable("custom_field_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("is_active").Ascending();

        Create.Table("custom_field_values")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("definition_id").AsGuid().NotNullable().ForeignKey("fk_custom_field_values_definition", "custom_field_definitions", "id")
            .WithColumn("scope_type").AsInt32().NotNullable()
            .WithColumn("entity_id").AsGuid().Nullable()
            .WithColumn("entity_key").AsString(64).NotNullable()
            .WithColumn("value_json").AsCustom("jsonb").NotNullable()
            .WithColumn("updated_by").AsString(256).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_custom_field_values_definition_entity")
            .OnTable("custom_field_values")
            .OnColumn("definition_id").Ascending()
            .OnColumn("entity_key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_custom_field_values_scope_entity")
            .OnTable("custom_field_values")
            .OnColumn("scope_type").Ascending()
            .OnColumn("entity_id").Ascending();

        Create.Table("custom_field_execution_access")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("definition_id").AsGuid().NotNullable().ForeignKey("fk_custom_field_execution_access_definition", "custom_field_definitions", "id")
            .WithColumn("task_id").AsGuid().Nullable().ForeignKey("fk_custom_field_execution_access_task", "automation_task_definitions", "id")
            .WithColumn("script_id").AsGuid().Nullable().ForeignKey("fk_custom_field_execution_access_script", "automation_script_definitions", "id")
            .WithColumn("can_read").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("can_write").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_custom_field_execution_access_def_task_script")
            .OnTable("custom_field_execution_access")
            .OnColumn("definition_id").Ascending()
            .OnColumn("task_id").Ascending()
            .OnColumn("script_id").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("custom_field_execution_access");
        Delete.Table("custom_field_values");
        Delete.Table("custom_field_definitions");
    }
}
