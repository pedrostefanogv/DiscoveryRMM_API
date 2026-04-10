using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260304_022)]
public class M022_AddConfigurationLocks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("locked_fields_json").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]");
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Column("locked_fields_json").Exists())
        {
            Delete.Column("locked_fields_json").FromTable("server_configurations");
        }
    }
}
