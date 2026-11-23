using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Formato físico da máquina no inventário de hardware: notebook × desktop ×
/// tablet × servidor.
///
/// Contexto: até aqui o painel só conseguia dizer se a máquina era Windows
/// Server (M272). Não havia NENHUM sinal de "é notebook", então o operador não
/// distinguia um desktop de um notebook na listagem nem sabia se a máquina
/// estava na tomada — informação que muda o diagnóstico (bateria viciada,
/// deploy adiado por energia, equipamento usado fora da mesa).
///
/// Todas as colunas são NULLABLE de propósito, com a MESMA convenção de
/// os_is_server: NULL = "não reportado" (agente antigo, VM sem WMI, ou coleta
/// inconclusiva) e NUNCA "não é notebook". Assim a migration é retrocompatível
/// e não exige backfill; o painel esconde o selo quando o campo é NULL e cai
/// para o comportamento atual.
///
/// A lista de baterias NÃO ganha tabela: vive no JSON de componentes
/// (hardware_components_json), como impressoras/startup/tarefas — é um punhado
/// de itens por máquina e não precisa de coluna própria.
///
/// ATENÇÃO: esta migration apenas DEFINE o schema (aplicada pelo MigrateUp no
/// boot da API). Nenhum ALTER é destrutivo.
/// </summary>
[Migration(20270127_294)]
public class M294_AddMachineTypeToAgentHardwareInfo : Migration
{
    private const string Table = "agent_hardware_info";

    /// <summary>Índice do futuro filtro "somente notebooks" na listagem de agentes.</summary>
    private const string IsLaptopIndex = "ix_hardware_is_laptop";

    public override void Up()
    {
        // "É notebook?" — derivado no agente (PCSystemType + presença de bateria).
        Alter.Table(Table)
            .AddColumn("is_laptop").AsBoolean().Nullable();

        // Vocabulário fechado: notebook | desktop | tablet | server | unknown.
        Alter.Table(Table)
            .AddColumn("machine_type").AsString(20).Nullable();

        // Sinais brutos usados na classificação, guardados para diagnóstico e
        // para reprocessar a regra sem re-depender do agente.
        Alter.Table(Table)
            .AddColumn("chassis_type").AsInt32().Nullable();

        Alter.Table(Table)
            .AddColumn("pc_system_type").AsInt32().Nullable();

        // Idempotente: o índice pode já existir em bases que o criaram por outra
        // via (mesmo padrão da M270/M272).
        if (!Schema.Table(Table).Index(IsLaptopIndex).Exists())
        {
            Create.Index(IsLaptopIndex)
                .OnTable(Table)
                .OnColumn("is_laptop").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Index(IsLaptopIndex).Exists())
            Delete.Index(IsLaptopIndex).OnTable(Table);

        Delete.Column("pc_system_type").FromTable(Table);
        Delete.Column("chassis_type").FromTable(Table);
        Delete.Column("machine_type").FromTable(Table);
        Delete.Column("is_laptop").FromTable(Table);
    }
}
