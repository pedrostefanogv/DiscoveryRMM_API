using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_004)]
public class M004_CreateAgentHardware : Migration
{
    public override void Up()
    {
        Create.Table("agent_hardware_info")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().Unique().ForeignKey("fk_hardware_agent", "agents", "id").Indexed("ix_hardware_agent_id")
            .WithColumn("manufacturer").AsString(200).Nullable()
            .WithColumn("model").AsString(200).Nullable()
            .WithColumn("serial_number").AsString(100).Nullable()
            .WithColumn("processor").AsString(300).Nullable()
            .WithColumn("processor_cores").AsInt32().Nullable()
            .WithColumn("processor_threads").AsInt32().Nullable()
            .WithColumn("total_memory_bytes").AsInt64().Nullable()
            .WithColumn("bios_version").AsString(200).Nullable()
            .WithColumn("collected_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

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
    }

    public override void Down()
    {
        Delete.Table("network_adapter_info");
        Delete.Table("disk_info");
        Delete.Table("agent_hardware_info");
    }
}
