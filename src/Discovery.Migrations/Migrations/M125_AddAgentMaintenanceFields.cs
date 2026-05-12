using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260512_125)]
public class M125_AddAgentMaintenanceFields : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("maintenance_enabled").Exists())
        {
            Alter.Table("agents")
                .AddColumn("maintenance_enabled").AsBoolean().WithDefaultValue(false);
        }

        if (!Schema.Table("agents").Column("maintenance_reason").Exists())
        {
            Alter.Table("agents")
                .AddColumn("maintenance_reason").AsString(500).Nullable();
        }

        if (!Schema.Table("agents").Column("maintenance_changed_at").Exists())
        {
            Alter.Table("agents")
                .AddColumn("maintenance_changed_at").AsCustom("timestamptz").Nullable();
        }

        if (!Schema.Table("agents").Column("maintenance_changed_by_user_id").Exists())
        {
            Alter.Table("agents")
                .AddColumn("maintenance_changed_by_user_id").AsGuid().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Column("maintenance_changed_by_user_id").Exists())
            Delete.Column("maintenance_changed_by_user_id").FromTable("agents");

        if (Schema.Table("agents").Column("maintenance_changed_at").Exists())
            Delete.Column("maintenance_changed_at").FromTable("agents");

        if (Schema.Table("agents").Column("maintenance_reason").Exists())
            Delete.Column("maintenance_reason").FromTable("agents");

        if (Schema.Table("agents").Column("maintenance_enabled").Exists())
            Delete.Column("maintenance_enabled").FromTable("agents");
    }
}
