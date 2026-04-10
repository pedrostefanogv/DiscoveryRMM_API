using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260307_036)]
public class M036_CreateReportTemplateHistory : Migration
{
    public override void Up()
    {
        Create.Table("report_template_history")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("template_id").AsGuid().NotNullable()
            .WithColumn("version").AsInt32().NotNullable()
            .WithColumn("change_type").AsString(32).NotNullable()
            .WithColumn("snapshot_json").AsCustom("jsonb").NotNullable()
            .WithColumn("changed_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("changed_by").AsString(256).Nullable();

        Create.Index("ix_report_template_history_template_version")
            .OnTable("report_template_history")
            .OnColumn("template_id")
            .Ascending()
            .OnColumn("version")
            .Descending();

        Create.Index("ix_report_template_history_changed_at")
            .OnTable("report_template_history")
            .OnColumn("changed_at")
            .Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_report_template_history_changed_at").OnTable("report_template_history");
        Delete.Index("ix_report_template_history_template_version").OnTable("report_template_history");
        Delete.Table("report_template_history");
    }
}
