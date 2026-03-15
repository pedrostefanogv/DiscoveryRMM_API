using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260315_067)]
public class M067_CreateSyncPingDeliveries : Migration
{
    public override void Up()
    {
        Create.Table("sync_ping_deliveries")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("event_id").AsGuid().NotNullable()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("resource").AsInt32().NotNullable()
            .WithColumn("revision").AsString(255).NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("sent_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("acknowledged_at").AsCustom("timestamptz").Nullable()
            .WithColumn("ack_metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("error_code").AsString(100).Nullable()
            .WithColumn("error_message").AsString(1000).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_sync_ping_deliveries_event_agent_revision")
            .OnTable("sync_ping_deliveries")
            .OnColumn("event_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("revision").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_sync_ping_deliveries_status_sent")
            .OnTable("sync_ping_deliveries")
            .OnColumn("status").Ascending()
            .OnColumn("sent_at").Ascending();

        Create.Index("ix_sync_ping_deliveries_agent_created")
            .OnTable("sync_ping_deliveries")
            .OnColumn("agent_id").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("sync_ping_deliveries");
    }
}
