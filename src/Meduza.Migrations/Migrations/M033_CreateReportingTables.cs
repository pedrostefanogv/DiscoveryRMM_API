using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_033)]
public class M033_CreateReportingTables : Migration
{
    public override void Up()
    {
        Create.Table("report_templates")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id")
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("dataset_type").AsInt32().NotNullable()
            .WithColumn("default_format").AsInt32().NotNullable()
            .WithColumn("layout_json").AsCustom("jsonb").NotNullable()
            .WithColumn("filters_json").AsCustom("jsonb").Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("version").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable();

        Create.Index("ix_report_templates_client")
            .OnTable("report_templates")
            .OnColumn("client_id");

        Create.Index("ix_report_templates_dataset")
            .OnTable("report_templates")
            .OnColumn("dataset_type");

        Create.Index("ix_report_templates_is_active")
            .OnTable("report_templates")
            .OnColumn("is_active");

        Create.Index("ix_report_templates_created_at")
            .OnTable("report_templates")
            .OnColumn("created_at");

        Create.Table("report_executions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("template_id").AsGuid().NotNullable().ForeignKey("report_templates", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("client_id").AsGuid().NotNullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("format").AsInt32().NotNullable()
            .WithColumn("filters_json").AsCustom("jsonb").Nullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("row_count").AsInt32().Nullable()
            .WithColumn("error_message").AsString(2000).Nullable()
            .WithColumn("execution_time_ms").AsInt32().Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("started_at").AsCustom("timestamptz").Nullable()
            .WithColumn("finished_at").AsCustom("timestamptz").Nullable()
            .WithColumn("created_by").AsString(256).Nullable();

        Create.Index("ix_report_executions_client")
            .OnTable("report_executions")
            .OnColumn("client_id");

        Create.Index("ix_report_executions_status")
            .OnTable("report_executions")
            .OnColumn("status");

        Create.Index("ix_report_executions_created_at")
            .OnTable("report_executions")
            .OnColumn("created_at");

        Create.Index("ix_report_executions_template")
            .OnTable("report_executions")
            .OnColumn("template_id");
    }

    public override void Down()
    {
        Delete.Index("ix_report_executions_template").OnTable("report_executions");
        Delete.Index("ix_report_executions_created_at").OnTable("report_executions");
        Delete.Index("ix_report_executions_status").OnTable("report_executions");
        Delete.Index("ix_report_executions_client").OnTable("report_executions");
        Delete.Table("report_executions");

        Delete.Index("ix_report_templates_created_at").OnTable("report_templates");
        Delete.Index("ix_report_templates_is_active").OnTable("report_templates");
        Delete.Index("ix_report_templates_dataset").OnTable("report_templates");
        Delete.Index("ix_report_templates_client").OnTable("report_templates");
        Delete.Table("report_templates");
    }
}
