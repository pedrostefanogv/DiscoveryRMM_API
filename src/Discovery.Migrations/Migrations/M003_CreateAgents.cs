using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_003)]
public class M003_CreateAgents : Migration
{
    public override void Up()
    {
        Create.Table("agents")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("site_id").AsGuid().NotNullable().ForeignKey("fk_agents_site", "sites", "id")
            .WithColumn("hostname").AsString(200).NotNullable()
            .WithColumn("display_name").AsString(200).Nullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(1) // Offline
            .WithColumn("operating_system").AsString(200).Nullable()
            .WithColumn("os_version").AsString(100).Nullable()
            .WithColumn("agent_version").AsString(50).Nullable()
            .WithColumn("last_ip_address").AsString(45).Nullable()
            .WithColumn("mac_address").AsString(17).Nullable()
            .WithColumn("last_seen_at").AsCustom("timestamptz").Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agents_site_id").OnTable("agents").OnColumn("site_id");
        Create.Index("ix_agents_hostname").OnTable("agents").OnColumn("hostname");
    }

    public override void Down()
    {
        Delete.Table("agents");
    }
}
