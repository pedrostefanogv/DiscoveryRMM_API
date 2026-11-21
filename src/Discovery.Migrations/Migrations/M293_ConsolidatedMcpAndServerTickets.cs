using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Promove `list_tickets` e `get_ticket_details` para server-side (`source = 'server'`)
/// com schemas completos, e semeia policies globais para as famílias consolidadas
/// do Agent (`software`, `event_log`, `local_memory`, `screen`).
/// </summary>
[Migration(20270126_293)]
public class M293_ConsolidatedMcpAndServerTickets : Migration
{
    private const string ListTicketsSchema = """
{
  "type": "object",
  "properties": {
    "status": {
      "type": "string",
      "enum": ["open", "closed"],
      "description": "Filtra por chamados abertos ('open') ou encerrados ('closed'). Omita para listar ambos."
    },
    "text": {
      "type": "string",
      "description": "Texto opcional para filtrar por título/descrição."
    },
    "limit": {
      "type": "integer",
      "description": "Máximo de itens retornados (1 a 50, padrão 20)."
    }
  },
  "required": [],
  "additionalProperties": false
}
""";

    private const string GetTicketDetailsSchema = """
{
  "type": "object",
  "properties": {
    "ticketId": {
      "type": "string",
      "format": "uuid",
      "description": "ID (GUID) do chamado."
    }
  },
  "required": ["ticketId"],
  "additionalProperties": false
}
""";

    public override void Up()
    {
        // 1. Promove list_tickets e get_ticket_details para server-side
        UpsertServerTool("list_tickets", "Lista chamados desta máquina no servidor (evita roundtrip ao agente).", 30, 30, ListTicketsSchema);
        UpsertServerTool("get_ticket_details", "Obtém os detalhes completos de um chamado específico no servidor.", 30, 30, GetTicketDetailsSchema);

        // 2. Semeia policies globais das famílias consolidadas do Agent
        SeedAgentTool("software", "Gerencia aplicativos e pacotes winget (search, list_installed, list_start_menu, pending_updates, actions, install, upgrade, upgrade_all, uninstall).", 10, 900);
        SeedAgentTool("event_log", "Consulta eventos, erros e falhas recentes do Windows Event Log (query, recent_errors, recent_crashes).", 20, 45);
        SeedAgentTool("local_memory", "Gerencia anotações locais da máquina (list, create, delete).", 20, 15);
        SeedAgentTool("screen", "Captura de tela, janelas abertas e permissão de captura (capture, list_windows, permission_status).", 10, 0);
    }

    private void UpsertServerTool(string name, string description, int maxCalls, int timeoutSec, string schemaJson)
    {
        var escDesc = description.Replace("'", "''");
        var escSchema = schemaJson.Replace("'", "''");
        Execute.Sql($"""
            UPDATE mcp_tool_policies
               SET source = 'server',
                   description = '{escDesc}',
                   argument_schema_json = '{escSchema}',
                   updated_at = NOW()
             WHERE tool_name = '{name}'
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;

            INSERT INTO mcp_tool_policies (
                id, tool_name, source, description, is_enabled, max_calls_per_minute, timeout_seconds,
                argument_schema_json, locked, created_at, updated_at
            )
            SELECT
                gen_random_uuid(), '{name}', 'server', '{escDesc}', TRUE, {maxCalls}, {timeoutSec},
                '{escSchema}', FALSE, NOW(), NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = '{name}' AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
            """);
    }

    private void SeedAgentTool(string name, string description, int maxCalls, int timeoutSec)
    {
        var escDesc = description.Replace("'", "''");
        Execute.Sql($"""
            INSERT INTO mcp_tool_policies (
                id, tool_name, source, description, is_enabled, max_calls_per_minute, timeout_seconds,
                locked, created_at, updated_at
            )
            SELECT
                gen_random_uuid(), '{name}', 'agent', '{escDesc}', TRUE, {maxCalls}, {timeoutSec},
                FALSE, NOW(), NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = '{name}' AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
            """);
    }

    public override void Down() { }
}
