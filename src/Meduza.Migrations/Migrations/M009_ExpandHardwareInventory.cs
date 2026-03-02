using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_009)]
public class M009_ExpandHardwareInventory : Migration
{
    public override void Up()
    {
        // Expandir agent_hardware_info com placa-mãe, processador e OS
        Alter.Table("agent_hardware_info")
            .AddColumn("motherboard_manufacturer").AsString(200).Nullable()
            .AddColumn("motherboard_model").AsString(200).Nullable()
            .AddColumn("motherboard_serial_number").AsString(100).Nullable()
            .AddColumn("processor_architecture").AsString(20).Nullable()
            .AddColumn("bios_manufacturer").AsString(200).Nullable()
            .AddColumn("os_name").AsString(200).Nullable()
            .AddColumn("os_version").AsString(100).Nullable()
            .AddColumn("os_build").AsString(100).Nullable()
            .AddColumn("os_architecture").AsString(20).Nullable();

        // Tabela de módulos de memória RAM individuais
        Create.Table("memory_module_info")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_memory_module_agent", "agents", "id")
            .WithColumn("slot").AsString(50).Nullable()
            .WithColumn("capacity_bytes").AsInt64().NotNullable()
            .WithColumn("speed_mhz").AsInt32().Nullable()
            .WithColumn("memory_type").AsString(50).Nullable()
            .WithColumn("manufacturer").AsString(200).Nullable()
            .WithColumn("part_number").AsString(100).Nullable()
            .WithColumn("serial_number").AsString(100).Nullable()
            .WithColumn("collected_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_memory_module_agent_id").OnTable("memory_module_info").OnColumn("agent_id");
    }

    public override void Down()
    {
        Delete.Table("memory_module_info");

        Delete.Column("motherboard_manufacturer").FromTable("agent_hardware_info");
        Delete.Column("motherboard_model").FromTable("agent_hardware_info");
        Delete.Column("motherboard_serial_number").FromTable("agent_hardware_info");
        Delete.Column("processor_architecture").FromTable("agent_hardware_info");
        Delete.Column("bios_manufacturer").FromTable("agent_hardware_info");
        Delete.Column("os_name").FromTable("agent_hardware_info");
        Delete.Column("os_version").FromTable("agent_hardware_info");
        Delete.Column("os_build").FromTable("agent_hardware_info");
        Delete.Column("os_architecture").FromTable("agent_hardware_info");
    }
}
