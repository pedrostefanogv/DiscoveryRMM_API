using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_102)]
public class M102_CreateTicketWatchers : Migration
{
    public override void Up()
    {
        Create.Table("ticket_watchers")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("added_by").AsString(255).Nullable()
            .WithColumn("added_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_ticket_watchers_ticket_id")
            .OnTable("ticket_watchers")
            .OnColumn("ticket_id");

        Create.UniqueConstraint("uq_ticket_watchers_ticket_user")
            .OnTable("ticket_watchers")
            .Columns("ticket_id", "user_id");
    }

    public override void Down()
    {
        Delete.Table("ticket_watchers");
    }
}
