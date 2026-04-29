using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260429_112)]
public class M112_AddIsBuiltInToReportTemplates : Migration
{
    public override void Up()
    {
        Alter.Table("report_templates")
            .AddColumn("is_built_in").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Index("ix_report_templates_is_built_in").OnTable("report_templates").OnColumn("is_built_in");
    }

    public override void Down()
    {
        Delete.Column("is_built_in").FromTable("report_templates");
    }
}
