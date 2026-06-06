using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_132)]
public class M132_AddAgentTokenLastNatsConnectedAt : Migration
{
    public override void Up()
    {
        Alter.Table("agent_tokens")
            .AddColumn("last_nats_connected_at").AsDateTimeOffset().Nullable();
    }

    public override void Down()
    {
        Delete.Column("last_nats_connected_at").FromTable("agent_tokens");
    }
}
