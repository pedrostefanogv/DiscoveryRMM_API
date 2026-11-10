using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seed das MCP tools de SERVIDOR para os SENSORES do agente (requisito "MCP para
/// consultar os sensores e histórico"):
///
///  • get_agent_sensors        — sensores da máquina e o estado CORRENTE
///  • get_agent_sensor_history — série temporal / timeline de UM sensor
///
/// Por que existe: até aqui o catálogo do servidor tinha ZERO tools de sensor. A
/// get_agent_metrics_history (M236) lê MONITORING EVENTS (alertas de CPU/memória/
/// disco), que é outra fonte de dados — quem pergunta "como está o sensor de
/// espaço em disco?" no chat não tinha resposta.
///
/// Sem uma policy GLOBAL habilitada o McpToolExecutor nega a execução
/// ("Tool ... não autorizada para este escopo") mesmo com o handler registrado.
/// O seed nasce habilitado (ambas são SOMENTE LEITURA) e passa a ser ligado/
/// desligado em /settings/mcp-tools por cliente/site/agente, como as demais.
///
/// Idempotente (NOT EXISTS no escopo global), no mesmo padrão da M236/M133/M169.
/// Timeouts e rate limits espelham o McpToolCatalogMetadata e a descrição
/// editorial da tela de governança:
///   get_agent_sensors        — 30/min, 30s (leitura agregada barata)
///   get_agent_sensor_history — 30/min, 60s (varre amostras/rollups de 90 dias)
/// </summary>
[Migration(20270117_285)]
public class M285_SeedSensorMcpTools : Migration
{
    /// <summary>Nomes semeados — usado por testes de coerência catálogo x seed.</summary>
    public static readonly string[] SeededToolNames =
    [
        "get_agent_sensors",
        "get_agent_sensor_history"
    ];

    private const string SensorsSchema = """
{
  "type": "object",
  "properties": {
    "sensorType": {
      "type": "string",
      "description": "Filtra pelo tipo do sensor (opcional). Ex.: DiskSpace, Cpu, Memory, Service, Ping, Script, SmartStatus, BitLocker, DefenderStatus, CertificateExpiry, EventLogRate, ADConsistency."
    },
    "status": {
      "type": "string",
      "description": "Filtra pelo status avaliado pelo SERVIDOR (opcional).",
      "enum": ["Pending", "Passing", "Warning", "Failing", "Unknown"]
    },
    "includeInactive": {
      "type": "boolean",
      "description": "Inclui sensores sem leitura recente (Pending/desatualizados). Padrão: false.",
      "default": false
    }
  },
  "required": [],
  "additionalProperties": false
}
""";

    private const string SensorHistorySchema = """
{
  "type": "object",
  "properties": {
    "sensorId": {
      "type": "string",
      "format": "uuid",
      "description": "GUID do sensor (OBRIGATÓRIO). Use get_agent_sensors para descobrir os IDs desta máquina."
    },
    "channel": {
      "type": "string",
      "description": "Filtra por UM canal numérico do sensor (opcional). Ex.: free_percent, used_percent."
    },
    "from": {
      "type": "string",
      "format": "date-time",
      "description": "Início da janela em ISO 8601/UTC (opcional). Padrão: 24 horas atrás."
    },
    "to": {
      "type": "string",
      "format": "date-time",
      "description": "Fim da janela em ISO 8601/UTC (opcional). Padrão: agora."
    },
    "targetKey": {
      "type": "string",
      "description": "Alvo específico quando o sensor cobre VÁRIOS alvos (opcional). Ex.: C:, D: em disco; host em ping."
    },
    "limit": {
      "type": "integer",
      "description": "Máximo de pontos retornados (1 a 200; padrão 50).",
      "minimum": 1,
      "maximum": 200,
      "default": 50
    }
  },
  "required": ["sensorId"],
  "additionalProperties": false
}
""";

    public override void Up()
    {
        // Leitura agregada da aba "Sensores": algumas consultas por agente.
        Seed("get_agent_sensors", SensorsSchema, maxCallsPerMinute: 30, timeoutSeconds: 30);

        // Histórico varre amostras brutas/rollups (retenção de 90 dias).
        Seed("get_agent_sensor_history", SensorHistorySchema, maxCallsPerMinute: 30, timeoutSeconds: 60);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE FROM mcp_tool_policies
             WHERE tool_name IN ('get_agent_sensors', 'get_agent_sensor_history')
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
