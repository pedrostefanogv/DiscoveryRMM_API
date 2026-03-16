using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_075)]
public class M075_AddMeshCentralIdentitySyncFieldsToUsers : Migration
{
    public override void Up()
    {
        Alter.Table("users")
            .AddColumn("meshcentral_user_id").AsString(256).Nullable()
            .AddColumn("meshcentral_username").AsString(100).Nullable()
            .AddColumn("meshcentral_last_synced_at").AsCustom("timestamptz").Nullable()
            .AddColumn("meshcentral_sync_status").AsString(32).Nullable()
            .AddColumn("meshcentral_sync_error").AsString(1024).Nullable();

        Create.Index("ix_users_meshcentral_user_id")
            .OnTable("users")
            .OnColumn("meshcentral_user_id").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_users_meshcentral_user_id").OnTable("users");

        Delete.Column("meshcentral_sync_error").FromTable("users");
        Delete.Column("meshcentral_sync_status").FromTable("users");
        Delete.Column("meshcentral_last_synced_at").FromTable("users");
        Delete.Column("meshcentral_username").FromTable("users");
        Delete.Column("meshcentral_user_id").FromTable("users");
    }
}
