using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20261123_296)]
public class M296_AddSoftDeleteToAutomationScripts : Migration
{
    private const string Table = "automation_script_definitions";
    private const string IndexName = "ix_automation_scripts_deleted_at";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("deleted_at").Exists())
        {
            Alter.Table(Table)
                .AddColumn("deleted_at").AsCustom("timestamptz").Nullable();
        }

        if (!Schema.Table(Table).Index(IndexName).Exists())
        {
            Create.Index(IndexName)
                .OnTable(Table)
                .OnColumn("deleted_at").Ascending();
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index(IndexName).Exists())
        {
            Delete.Index(IndexName).OnTable(Table);
        }

        if (Schema.Table(Table).Column("deleted_at").Exists())
        {
            Delete.Column("deleted_at").FromTable(Table);
        }
    }
}
