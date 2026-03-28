using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260327_091)]
public class M091_AddZeroTouchPendingToAgents : Migration
{
    public override void Up()
    {
        Alter.Table("agents")
            .AddColumn("zero_touch_pending").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("zero_touch_pending").FromTable("agents");
    }
}
