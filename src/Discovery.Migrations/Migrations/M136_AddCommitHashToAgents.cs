using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260715_136)]
public class M136_AddCommitHashToAgents : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("commit_hash").Exists())
        {
            Alter.Table("agents")
                .AddColumn("commit_hash").AsString(64).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Column("commit_hash").Exists())
        {
            Delete.Column("commit_hash").FromTable("agents");
        }
    }
}
