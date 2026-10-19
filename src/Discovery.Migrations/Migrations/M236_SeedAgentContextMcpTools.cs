using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seed das MCP tools de SERVIDOR que trazem contexto operacional do agente
/// para o chat (Fase 0 do plano de novos MCPs):
///
///  • get_agent_alerts           — alertas PSADT da máquina (somente leitura)
///  • get_agent_labels           — labels automáticas/manuais da máquina
///  • get_agent_metrics_history  — histórico de eventos de monitoramento
///
/// Sem uma policy GLOBAL habilitada o McpToolExecutor nega a execução
/// ("Tool ... não autorizada para este escopo") mesmo com o handler registrado.
/// O seed nasce habilitado (leitura) e passa a ser ligado/desligado em
/// /settings/mcp-tools por cliente/site/agente, como as demais tools.
///
/// Idempotente (NOT EXISTS no escopo global), no mesmo padrão das M133/M169.
/// Timeouts e rate limits espelham o McpToolCatalogMetadata e a descrição
/// editorial da tela de governança.
/// </summary>
[Migration(20261221_236)]
public class M236_SeedAgentContextMcpTools : Migration
{
    /// <summary>Nomes semeados — usado por testes de coerência catálogo x seed.</summary>
    public static readonly string[] SeededToolNames =
    [
        "get_agent_alerts",
        "get_agent_labels",
        "get_agent_metrics_history"
    ];

    private const string AlertsSchema = """
{
  "type": "object",
  "properties": {
    "status": {
      "type": "string",
      "description": "Filtra pelo status do alerta (opcional).",
      "enum": ["Draft", "Scheduled", "Dispatching", "Dispatched", "Expired", "Cancelled"]
    },
    "ticketId": {
      "type": "string",
      "format": "uuid",
      "description": "Filtra alertas vinculados a um chamado (opcional)."
    },
    "cursor": {
      "type": "string",
      "description": "Cursor opaco para a próxima página (opcional)."
    },
    "limit": {
      "type": "integer",
      "description": "Máximo de alertas retornados (1 a 20; padrão 20).",
      "minimum": 1,
      "maximum": 20,
      "default": 20
    }
  },
  "required": [],
  "additionalProperties": false
}
""";

    private const string LabelsSchema = """
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
""";

    private const string MetricsHistorySchema = """
{
  "type": "object",
  "properties": {
    "limit": {
      "type": "integer",
      "description": "Máximo de eventos retornados (1 a 200; padrão 50).",
      "minimum": 1,
      "maximum": 200,
      "default": 50
    },
    "cursor": {
      "type": "string",
      "description": "Cursor opaco para a próxima página (opcional)."
    }
  },
  "required": [],
  "additionalProperties": false
}
""";

    public override void Up()
    {
        // Leitura barata de definir as referências; as queries do agente já
        // respondem em poucos segundos.
        Seed("get_agent_alerts", AlertsSchema, maxCallsPerMinute: 30, timeoutSeconds: 30);

        // Labels são uma consulta pequena por agente.
        Seed("get_agent_labels", LabelsSchema, maxCallsPerMinute: 20, timeoutSeconds: 20);

        // Histórico pode varrer mais linhas; teto maior e limite de payload.
        Seed("get_agent_metrics_history", MetricsHistorySchema, maxCallsPerMinute: 30, timeoutSeconds: 60);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE FROM mcp_tool_policies
             WHERE tool_name IN ('get_agent_alerts', 'get_agent_labels', 'get_agent_metrics_history')
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;
            """);
    }

    private void Seed(string toolName, string argumentSchemaJson, int maxCallsPerMinute, int timeoutSeconds)
    {
        var schema = argumentSchemaJson.Replace("'", "''");

        Execute.Sql($"""
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, '{toolName}', true,
                '{schema}',
                {maxCallsPerMinute}, {timeoutSeconds}, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = '{toolName}'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
            """);
    }
}
