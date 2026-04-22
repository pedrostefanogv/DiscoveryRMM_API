using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_104)]
public class M104_CreateTicketRemoteSessions : Migration
{
    public override void Up()
    {
        Create.Table("ticket_remote_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
            .WithColumn("agent_id").AsGuid().Nullable()
            .WithColumn("mesh_node_id").AsString(512).Nullable()
            .WithColumn("session_url").AsString(2048).Nullable()
            .WithColumn("started_by").AsString(255).Nullable()
            .WithColumn("started_at").AsDateTimeOffset().NotNullable()
            .WithColumn("ended_at").AsDateTimeOffset().Nullable()
            .WithColumn("duration_seconds").AsInt32().Nullable()
            .WithColumn("note").AsString(2000).Nullable();

        Create.Index("ix_ticket_remote_sessions_ticket_id")
            .OnTable("ticket_remote_sessions")
            .OnColumn("ticket_id");
    }

    public override void Down()
    {
        Delete.Table("ticket_remote_sessions");
    }
}
