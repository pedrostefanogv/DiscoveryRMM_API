using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260715_134)]
public class M134_AddCommitHashToAgentUpdateBuilds : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agent_update_builds").Column("commit_hash").Exists())
        {
            Alter.Table("agent_update_builds")
                .AddColumn("commit_hash").AsString(64).NotNullable().WithDefaultValue("unknown");
        }
    }

    public override void Down()
    {
        if (Schema.Table("agent_update_builds").Column("commit_hash").Exists())
        {
            Delete.Column("commit_hash").FromTable("agent_update_builds");
        }
    }
}
