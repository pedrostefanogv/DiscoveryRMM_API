using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_098)]
public class M098_CreateTicketSavedViews : Migration
{
    public override void Up()
    {
        Create.Table("ticket_saved_views")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().Nullable()
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("filter_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
            .WithColumn("is_shared").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_saved_views_user_id")
            .OnTable("ticket_saved_views")
            .OnColumn("user_id");

        Create.Index("ix_ticket_saved_views_is_shared")
            .OnTable("ticket_saved_views")
            .OnColumn("is_shared");
    }

    public override void Down()
    {
        Delete.Table("ticket_saved_views");
    }
}
