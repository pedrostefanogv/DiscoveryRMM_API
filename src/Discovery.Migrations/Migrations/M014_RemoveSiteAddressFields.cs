using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260303_014)]
public class M014_RemoveSiteAddressFields : Migration
{
    public override void Up()
    {
        if (Schema.Table("sites").Column("address").Exists())
        {
            Delete.Column("address").FromTable("sites");
        }

        if (Schema.Table("sites").Column("city").Exists())
        {
            Delete.Column("city").FromTable("sites");
        }

        if (Schema.Table("sites").Column("state").Exists())
        {
            Delete.Column("state").FromTable("sites");
        }

        if (Schema.Table("sites").Column("zip_code").Exists())
        {
            Delete.Column("zip_code").FromTable("sites");
        }
    }

    public override void Down()
    {
        if (!Schema.Table("sites").Column("address").Exists())
        {
            Alter.Table("sites").AddColumn("address").AsString(500).Nullable();
        }

        if (!Schema.Table("sites").Column("city").Exists())
        {
            Alter.Table("sites").AddColumn("city").AsString(100).Nullable();
        }

        if (!Schema.Table("sites").Column("state").Exists())
        {
            Alter.Table("sites").AddColumn("state").AsString(50).Nullable();
        }

        if (!Schema.Table("sites").Column("zip_code").Exists())
        {
            Alter.Table("sites").AddColumn("zip_code").AsString(20).Nullable();
        }
    }
}
