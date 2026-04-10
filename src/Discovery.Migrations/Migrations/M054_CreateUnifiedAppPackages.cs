using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_054)]
public class M054_CreateUnifiedAppPackages : Migration
{
    public override void Up()
    {
        Create.Table("app_packages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("installation_type").AsInt32().NotNullable()
            .WithColumn("package_id").AsString(300).NotNullable()
            .WithColumn("name").AsString(500).NotNullable()
            .WithColumn("publisher").AsString(500).Nullable()
            .WithColumn("version").AsString(100).Nullable()
            .WithColumn("description").AsCustom("text").Nullable()
            .WithColumn("icon_url").AsString(2000).Nullable()
            .WithColumn("site_url").AsString(2000).Nullable()
            .WithColumn("install_command").AsString(1000).Nullable()
            .WithColumn("metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("file_object_key").AsString(1000).Nullable()
            .WithColumn("file_bucket").AsString(200).Nullable()
            .WithColumn("file_public_url").AsString(2000).Nullable()
            .WithColumn("file_content_type").AsString(200).Nullable()
            .WithColumn("file_size_bytes").AsInt64().Nullable()
            .WithColumn("file_checksum").AsString(200).Nullable()
            .WithColumn("source_generated_at").AsCustom("timestamptz").Nullable()
            .WithColumn("last_updated").AsCustom("timestamptz").Nullable()
            .WithColumn("synced_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_app_packages_installation_package")
            .OnTable("app_packages")
            .OnColumn("installation_type").Ascending()
            .OnColumn("package_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_app_packages_name")
            .OnTable("app_packages")
            .OnColumn("name").Ascending();

        Create.Index("ix_app_packages_installation_synced")
            .OnTable("app_packages")
            .OnColumn("installation_type").Ascending()
            .OnColumn("synced_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("app_packages");
    }
}
