using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260312_058)]
public class M058_AddDescriptionToAgentLabelRules : Migration
{
    public override void Up()
    {
        Alter.Table("agent_label_rules")
            .AddColumn("description").AsString(2000).Nullable();
    }

    public override void Down()
    {
        Delete.Column("description")
            .FromTable("agent_label_rules");
    }
}
