using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona ao inventário de software por agente os campos de atualização
/// pendente reportados pelo agente (winget upgrade / choco outdated):
/// available_version, update_available e update_source.
/// </summary>
[Migration(20260921_153)]
public class M153_AddSoftwareUpdateFields : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agent_software_inventory").Exists())
            return;

        if (!Schema.Table("agent_software_inventory").Column("available_version").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("available_version").AsString(120).Nullable();
        }

        if (!Schema.Table("agent_software_inventory").Column("update_available").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("update_available").AsBoolean().NotNullable().WithDefaultValue(false);
        }

        if (!Schema.Table("agent_software_inventory").Column("update_source").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("update_source").AsString(40).Nullable();
        }

        if (!Schema.Table("agent_software_inventory").Column("update_package_id").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("update_package_id").AsString(1000).Nullable();
        }
    }

    public override void Down()
    {
        if (!Schema.Table("agent_software_inventory").Exists())
            return;

        if (Schema.Table("agent_software_inventory").Column("update_package_id").Exists())
            Delete.Column("update_package_id").FromTable("agent_software_inventory");

        if (Schema.Table("agent_software_inventory").Column("update_source").Exists())
            Delete.Column("update_source").FromTable("agent_software_inventory");

        if (Schema.Table("agent_software_inventory").Column("update_available").Exists())
            Delete.Column("update_available").FromTable("agent_software_inventory");

        if (Schema.Table("agent_software_inventory").Column("available_version").Exists())
            Delete.Column("available_version").FromTable("agent_software_inventory");
    }
}
