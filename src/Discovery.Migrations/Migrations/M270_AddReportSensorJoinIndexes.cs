using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Índices de apoio ao join de sensores nos relatórios (dataset AgentSensors, 29).
///
/// A consulta do relatório de sensores (ReportDatasetQueryService.QueryAgentSensorsAsync)
/// filtra por status/severidade/janela de last_run_at, ordena por último run e faz
/// join com sensor_definitions. Antes desta migration os únicos índices da tabela
/// eram:
///   - ux_agent_sensor_states_agent_sensor_target (agent_id, sensor_id, target_key) UNIQUE
///   - ix_agent_sensor_states_agent_status         (agent_id, status)
/// Nenhum deles serve ao recorte por PERÍODO (last_run_at) combinado com o filtro
/// de status, que é o padrão do relatório "Sensores em Falha por Agente".
///
/// IMPORTANTE: os dois índices citados acima JÁ EXISTEM (criados em M260 e
/// declarados em DiscoveryDbContext.Sensors.cs) — esta migration NÃO os recria.
/// O Up cria apenas o que faltava e é idempotente: cada índice só é criado se
/// ainda não existir, para poder rodar em bases que já receberam o índice por
/// outra via.
/// </summary>
[Migration(20270105_270)]
public class M270_AddReportSensorJoinIndexes : Migration
{
    private const string StatesTable = "agent_sensor_states";
    private const string DefinitionsTable = "sensor_definitions";

    /// <summary>Período + status: o recorte do relatório de sensores em falha.</summary>
    private const string StateStatusLastRunIndex = "ix_agent_sensor_states_status_last_run";

    /// <summary>Período isolado, para relatórios sem filtro de status.</summary>
    private const string StateLastRunIndex = "ix_agent_sensor_states_last_run_at";

    /// <summary>Join state.sensor_id -> sensor_definitions.id filtrado por ativos.</summary>
    private const string DefinitionActiveIndex = "ix_sensor_definitions_active_name";

    public override void Up()
    {
        // (status, last_run_at DESC): cobre "sensores em falha no período".
        if (!Schema.Table(StatesTable).Index(StateStatusLastRunIndex).Exists())
        {
            Create.Index(StateStatusLastRunIndex)
                .OnTable(StatesTable)
                .OnColumn("status").Ascending()
                .OnColumn("last_run_at").Descending();
        }

        // (last_run_at DESC): recorte puramente temporal.
        if (!Schema.Table(StatesTable).Index(StateLastRunIndex).Exists())
        {
            Create.Index(StateLastRunIndex)
                .OnTable(StatesTable)
                .OnColumn("last_run_at").Descending();
        }

        // Definições ativas por nome: o relatório lista nome do sensor e só
        // considera definições vivas (deleted_at IS NULL).
        if (!Schema.Table(DefinitionsTable).Index(DefinitionActiveIndex).Exists())
        {
            Create.Index(DefinitionActiveIndex)
                .OnTable(DefinitionsTable)
                .OnColumn("deleted_at").Ascending()
                .OnColumn("name").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table(StatesTable).Index(StateStatusLastRunIndex).Exists())
            Delete.Index(StateStatusLastRunIndex).OnTable(StatesTable);

        if (Schema.Table(StatesTable).Index(StateLastRunIndex).Exists())
            Delete.Index(StateLastRunIndex).OnTable(StatesTable);

        if (Schema.Table(DefinitionsTable).Index(DefinitionActiveIndex).Exists())
            Delete.Index(DefinitionActiveIndex).OnTable(DefinitionsTable);
    }
}
