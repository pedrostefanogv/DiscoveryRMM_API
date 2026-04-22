using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260304_023)]
public class M023_AddClientSiteConfigurationLocks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("client_configurations").Column("locked_fields_json").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]");
        }

        if (!Schema.Table("site_configurations").Column("locked_fields_json").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]");
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Column("locked_fields_json").Exists())
        {
            Delete.Column("locked_fields_json").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Column("locked_fields_json").Exists())
        {
            Delete.Column("locked_fields_json").FromTable("client_configurations");
        }
    }
}
