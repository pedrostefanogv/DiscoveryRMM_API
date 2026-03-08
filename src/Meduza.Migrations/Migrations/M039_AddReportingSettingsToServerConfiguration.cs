using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_039)]
public class M039_AddReportingSettingsToServerConfiguration : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("reporting_settings_json").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("reporting_settings_json").AsCustom("text").NotNullable().WithDefaultValue("{}");
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Column("reporting_settings_json").Exists())
        {
            Delete.Column("reporting_settings_json").FromTable("server_configurations");
        }
    }
}
