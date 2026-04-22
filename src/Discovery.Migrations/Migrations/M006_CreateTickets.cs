using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_006)]
public class M006_CreateTickets : Migration
{
    public override void Up()
    {
        Create.Table("tickets")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable().ForeignKey("fk_tickets_client", "clients", "id")
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_tickets_site", "sites", "id")
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("fk_tickets_agent", "agents", "id")
            .WithColumn("title").AsString(500).NotNullable()
            .WithColumn("description").AsString(int.MaxValue).NotNullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0) // Open
            .WithColumn("priority").AsInt32().NotNullable().WithDefaultValue(1) // Medium
            .WithColumn("assigned_to").AsString(200).Nullable()
            .WithColumn("category").AsString(100).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("closed_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_tickets_client_id").OnTable("tickets").OnColumn("client_id");
        Create.Index("ix_tickets_status").OnTable("tickets").OnColumn("status");

        Create.Table("ticket_comments")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable().ForeignKey("fk_comments_ticket", "tickets", "id")
            .WithColumn("author").AsString(200).NotNullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("is_internal").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_comments_ticket_id").OnTable("ticket_comments").OnColumn("ticket_id");
    }

    public override void Down()
    {
        Delete.Table("ticket_comments");
        Delete.Table("tickets");
    }
}
