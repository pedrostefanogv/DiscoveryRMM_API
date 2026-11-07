using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Identidade do SO no inventário de hardware: edição, "é servidor?" e
/// associação a domínio.
///
/// Contexto (Windows Server): o agente já enviava <c>osEdition</c> no envelope
/// de hardware, mas o servidor descartava o campo — não havia coluna nem no
/// modelo nem no DTO. Sem ele o site não consegue distinguir um controlador de
/// domínio de uma estação de trabalho, nem oferecer o ícone e os filtros de
/// Windows Server.
///
/// Todas as colunas são NULLABLE de propósito: agentes antigos não enviam os
/// campos e NULL significa "não reportado" — nunca "não é servidor". Assim a
/// migration é retrocompatível e não exige backfill.
///
/// ATENÇÃO: esta migration apenas DEFINE o schema (é aplicada pelo MigrateUp no
/// boot da API). Nenhum ALTER é destrutivo: só adiciona colunas novas e um
/// índice de filtro.
/// </summary>
[Migration(20270107_272)]
public class M272_AddOsIdentityToAgentHardwareInfo : Migration
{
    private const string Table = "agent_hardware_info";

    /// <summary>Índice do filtro "somente servidores" na listagem de agentes.</summary>
    private const string IsServerIndex = "ix_hardware_os_is_server";

    public override void Up()
    {
        // Edição do SO (ex.: "Windows Server 2022 Datacenter").
        Alter.Table(Table)
            .AddColumn("os_edition").AsString(100).Nullable();

        // "É Windows Server?" — derivado no agente (ProductType).
        Alter.Table(Table)
            .AddColumn("os_is_server").AsBoolean().Nullable();

        // ProductType bruto do Windows (1=Workstation, 2=DomainController,
        // 3=Server/NT). Texto para preservar o valor original do agente.
        Alter.Table(Table)
            .AddColumn("os_product_type").AsString(50).Nullable();

        // Associação a domínio / grupo de trabalho.
        Alter.Table(Table)
            .AddColumn("os_domain").AsString(255).Nullable()
            .AddColumn("os_workgroup").AsString(255).Nullable()
            .AddColumn("os_part_of_domain").AsBoolean().Nullable();

        // Idempotente: o índice pode já existir em bases que o criaram por outra
        // via (mesmo padrão da M270).
        if (!Schema.Table(Table).Index(IsServerIndex).Exists())
        {
            Create.Index(IsServerIndex)
                .OnTable(Table)
                .OnColumn("os_is_server").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Index(IsServerIndex).Exists())
            Delete.Index(IsServerIndex).OnTable(Table);

        Delete.Column("os_part_of_domain").FromTable(Table);
        Delete.Column("os_workgroup").FromTable(Table);
        Delete.Column("os_domain").FromTable(Table);
        Delete.Column("os_product_type").FromTable(Table);
        Delete.Column("os_is_server").FromTable(Table);
        Delete.Column("os_edition").FromTable(Table);
    }
}
