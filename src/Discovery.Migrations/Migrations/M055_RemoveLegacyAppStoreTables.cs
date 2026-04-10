using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_055)]
public class M055_RemoveLegacyAppStoreTables : Migration
{
    public override void Up()
    {
        if (Schema.Table("winget_packages").Exists())
            Delete.Table("winget_packages");

        if (Schema.Table("chocolatey_packages").Exists())
            Delete.Table("chocolatey_packages");
    }

    public override void Down()
    {
        if (!Schema.Table("chocolatey_packages").Exists())
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

        if (!Schema.Table("winget_packages").Exists())
        {
            Create.Table("winget_packages")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("package_id").AsString(300).NotNullable()
                .WithColumn("name").AsString(500).NotNullable()
                .WithColumn("publisher").AsString(500).NotNullable()
                .WithColumn("version").AsString(100).NotNullable()
                .WithColumn("description").AsCustom("text").NotNullable()
                .WithColumn("homepage").AsString(2000).NotNullable()
                .WithColumn("license").AsString(500).NotNullable()
                .WithColumn("category").AsString(250).NotNullable()
                .WithColumn("icon").AsString(2000).NotNullable()
                .WithColumn("install_command").AsString(1000).NotNullable()
                .WithColumn("tags").AsCustom("text").NotNullable()
                .WithColumn("installer_urls_json").AsCustom("text").NotNullable()
                .WithColumn("last_updated").AsCustom("timestamptz").Nullable()
                .WithColumn("source_generated_at").AsCustom("timestamptz").Nullable()
                .WithColumn("synced_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Create.Index("ux_winget_packages_package_id")
                .OnTable("winget_packages")
                .OnColumn("package_id").Ascending()
                .WithOptions().Unique();

            Create.Index("ix_winget_packages_name")
                .OnTable("winget_packages")
                .OnColumn("name").Ascending();

            Create.Index("ix_winget_packages_synced_at")
                .OnTable("winget_packages")
                .OnColumn("synced_at").Descending();
        }
    }
}
