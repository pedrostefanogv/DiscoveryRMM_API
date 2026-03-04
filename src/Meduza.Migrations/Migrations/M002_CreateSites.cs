using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_002)]
public class M002_CreateSites : Migration
{
    public override void Up()
    {
        Create.Table("sites")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable().ForeignKey("fk_sites_client", "clients", "id")
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("notes").AsString(2000).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_sites_client_id").OnTable("sites").OnColumn("client_id");
    }

    public override void Down()
    {
        Delete.Table("sites");
    }
}
