using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260311_048)]
public class M048_CreateChocolateyPackages : Migration
{
    public override void Up()
    {
        Create.Table("chocolatey_packages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("package_id").AsString(300).NotNullable()
            .WithColumn("name").AsString(500).NotNullable()
            .WithColumn("publisher").AsString(500).NotNullable()
            .WithColumn("version").AsString(100).NotNullable()
            .WithColumn("description").AsCustom("text").NotNullable()
            .WithColumn("homepage").AsString(2000).NotNullable()
            .WithColumn("license_url").AsString(2000).NotNullable()
            .WithColumn("tags").AsString(2000).NotNullable()
            .WithColumn("download_count").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("last_updated").AsCustom("timestamptz").Nullable()
            .WithColumn("synced_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_chocolatey_packages_package_id")
            .OnTable("chocolatey_packages")
            .OnColumn("package_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_chocolatey_packages_name")
            .OnTable("chocolatey_packages")
            .OnColumn("name").Ascending();

        Create.Index("ix_chocolatey_packages_synced_at")
            .OnTable("chocolatey_packages")
            .OnColumn("synced_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("chocolatey_packages");
    }
}
