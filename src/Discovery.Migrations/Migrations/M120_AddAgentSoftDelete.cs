using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260504_120)]
public class M120_AddAgentSoftDelete : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("deleted_at").Exists())
        {
            Alter.Table("agents")
                .AddColumn("deleted_at").AsCustom("timestamptz").Nullable();
        }

        if (!Schema.Table("agents").Index("ix_agents_deleted_at").Exists())
        {
            Create.Index("ix_agents_deleted_at")
                .OnTable("agents")
                .OnColumn("deleted_at").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Index("ix_agents_deleted_at").Exists())
        {
            Delete.Index("ix_agents_deleted_at").OnTable("agents");
        }

        if (Schema.Table("agents").Column("deleted_at").Exists())
        {
            Delete.Column("deleted_at").FromTable("agents");
        }
    }
}
