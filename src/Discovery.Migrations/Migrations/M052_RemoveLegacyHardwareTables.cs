using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_052)]
public class M052_RemoveLegacyHardwareTables : Migration
{
    public override void Up()
    {
        Delete.Table("network_adapter_info");
        Delete.Table("memory_module_info");
        Delete.Table("disk_info");
    }

    public override void Down()
    {
        Create.Table("disk_info")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_disk_agent", "agents", "id")
            .WithColumn("drive_letter").AsString(10).NotNullable()
            .WithColumn("label").AsString(200).Nullable()
            .WithColumn("file_system").AsString(50).Nullable()
            .WithColumn("total_size_bytes").AsInt64().NotNullable()
            .WithColumn("free_space_bytes").AsInt64().NotNullable()
            .WithColumn("media_type").AsString(50).Nullable()
            .WithColumn("collected_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.Index("ix_disk_info_agent_id").OnTable("disk_info").OnColumn("agent_id");

        Create.Table("network_adapter_info")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_network_agent", "agents", "id")
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("mac_address").AsString(17).Nullable()
            .WithColumn("ip_address").AsString(45).Nullable()
            .WithColumn("subnet_mask").AsString(45).Nullable()
            .WithColumn("gateway").AsString(45).Nullable()
            .WithColumn("dns_servers").AsString(500).Nullable()
            .WithColumn("is_dhcp_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("adapter_type").AsString(50).Nullable()
            .WithColumn("speed").AsString(50).Nullable()
            .WithColumn("collected_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.Index("ix_network_adapter_agent_id").OnTable("network_adapter_info").OnColumn("agent_id");

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
}
