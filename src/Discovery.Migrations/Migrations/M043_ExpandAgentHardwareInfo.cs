using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260308_043)]
public class M043_ExpandAgentHardwareInfo : Migration
{
    public override void Up()
    {
        // GPU - Adicionar informações de placa de vídeo
        Alter.Table("agent_hardware_info")
            .AddColumn("gpu_model").AsString(300).Nullable()
            .AddColumn("gpu_memory_bytes").AsInt64().Nullable()
            .AddColumn("gpu_driver_version").AsString(100).Nullable();

        // Contador de discos
        Alter.Table("agent_hardware_info")
            .AddColumn("total_disks_count").AsInt32().Nullable();

        // Expandir informações de BIOS
        Alter.Table("agent_hardware_info")
            .AddColumn("bios_date").AsString(50).Nullable()
            .AddColumn("bios_serial_number").AsString(100).Nullable();

        // Expandir informações de processador
        Alter.Table("agent_hardware_info")
            .AddColumn("processor_tdp_watts").AsInt32().Nullable()
            .AddColumn("processor_socket").AsString(50).Nullable()
            .AddColumn("processor_frequency_ghz").AsDecimal(5, 2).Nullable()
            .AddColumn("processor_release_date").AsString(50).Nullable();

        // Índices para melhor performance nas queries de relatórios
        Create.Index("ix_hardware_collected_at").OnTable("agent_hardware_info").OnColumn("collected_at");
        Create.Index("ix_hardware_os_name").OnTable("agent_hardware_info").OnColumn("os_name");
        Create.Index("ix_hardware_processor").OnTable("agent_hardware_info").OnColumn("processor");
    }

    public override void Down()
    {
        // Remover índices
        Delete.Index("ix_hardware_collected_at").OnTable("agent_hardware_info");
        Delete.Index("ix_hardware_os_name").OnTable("agent_hardware_info");
        Delete.Index("ix_hardware_processor").OnTable("agent_hardware_info");

        // Remover colunas de processador
        Delete.Column("processor_release_date").FromTable("agent_hardware_info");
        Delete.Column("processor_frequency_ghz").FromTable("agent_hardware_info");
        Delete.Column("processor_socket").FromTable("agent_hardware_info");
        Delete.Column("processor_tdp_watts").FromTable("agent_hardware_info");

        // Remover colunas de BIOS
        Delete.Column("bios_serial_number").FromTable("agent_hardware_info");
        Delete.Column("bios_date").FromTable("agent_hardware_info");

        // Remover contador de discos
        Delete.Column("total_disks_count").FromTable("agent_hardware_info");

        // Remover colunas de GPU
        Delete.Column("gpu_driver_version").FromTable("agent_hardware_info");
        Delete.Column("gpu_memory_bytes").FromTable("agent_hardware_info");
        Delete.Column("gpu_model").FromTable("agent_hardware_info");
    }
}
