using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260307_037)]
public class M037_CreateAppNotifications : Migration
{
    public override void Up()
    {
        Create.Table("app_notifications")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("event_type").AsString(120).NotNullable()
            .WithColumn("topic").AsString(120).NotNullable()
            .WithColumn("recipient_user_id").AsGuid().Nullable()
            .WithColumn("recipient_key").AsString(256).Nullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("message").AsString(2000).NotNullable()
            .WithColumn("payload_json").AsCustom("jsonb").Nullable()
            .WithColumn("is_read").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("read_at").AsCustom("timestamptz").Nullable()
            .WithColumn("created_by").AsString(256).Nullable();

        Create.Index("ix_app_notifications_created_at")
            .OnTable("app_notifications")
            .OnColumn("created_at")
            .Descending();

        Create.Index("ix_app_notifications_topic_created")
            .OnTable("app_notifications")
            .OnColumn("topic")
            .Ascending()
            .OnColumn("created_at")
            .Descending();

        Create.Index("ix_app_notifications_user_read_created")
            .OnTable("app_notifications")
            .OnColumn("recipient_user_id")
            .Ascending()
            .OnColumn("is_read")
            .Ascending()
            .OnColumn("created_at")
            .Descending();

        Create.Index("ix_app_notifications_key_read_created")
            .OnTable("app_notifications")
            .OnColumn("recipient_key")
            .Ascending()
            .OnColumn("is_read")
            .Ascending()
            .OnColumn("created_at")
            .Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_app_notifications_key_read_created").OnTable("app_notifications");
        Delete.Index("ix_app_notifications_user_read_created").OnTable("app_notifications");
        Delete.Index("ix_app_notifications_topic_created").OnTable("app_notifications");
        Delete.Index("ix_app_notifications_created_at").OnTable("app_notifications");
        Delete.Table("app_notifications");
    }
}
