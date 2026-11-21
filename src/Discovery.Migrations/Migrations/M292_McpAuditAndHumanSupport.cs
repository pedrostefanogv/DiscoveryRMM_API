using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria a trilha de auditoria dedicada de execuções MCP (`mcp_tool_audit_logs`)
/// e semeia a policy global da tool server-side `request_human_support`.
/// </summary>
[Migration(20270125_292)]
public class M292_McpAuditAndHumanSupport : Migration
{
    private const string AuditTable = "mcp_tool_audit_logs";

    private const string RequestHumanSupportSchema = """
{
  "type": "object",
  "properties": {
    "summary": {
      "type": "string",
      "description": "Resumo estruturado do problema, passos já tentados e diagnósticos coletados (OBRIGATÓRIO)."
    },
    "reason": {
      "type": "string",
      "description": "Motivo do escalonamento para atendimento humano (opcional)."
    },
    "urgency": {
      "type": "string",
      "enum": ["low", "medium", "high", "critical"],
      "description": "Urgência percebida (opcional, padrão medium)."
    },
    "ticketId": {
      "type": "string",
      "format": "uuid",
      "description": "ID de um chamado existente para anexar o escalonamento como comentário (opcional)."
    }
  },
  "required": ["summary"],
  "additionalProperties": false
}
""";

    public override void Up()
    {
        if (!Schema.Table(AuditTable).Exists())
        {
            Create.Table(AuditTable)
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("tool_name").AsString(128).NotNullable()
                .WithColumn("origin").AsString(16).NotNullable()
                .WithColumn("client_id").AsGuid().Nullable()
                .WithColumn("site_id").AsGuid().Nullable()
                .WithColumn("agent_id").AsGuid().Nullable()
                .WithColumn("session_id").AsGuid().Nullable()
                .WithColumn("status").AsString(24).NotNullable()
                .WithColumn("duration_ms").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("error_message").AsString(512).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable();

            Create.Index("ix_mcp_tool_audit_logs_created_at")
                .OnTable(AuditTable).OnColumn("created_at").Descending();
            Create.Index("ix_mcp_tool_audit_logs_tool_created")
                .OnTable(AuditTable).OnColumn("tool_name").Ascending().OnColumn("created_at").Descending();
        }

        // Seed idempotente da policy global para request_human_support
        Execute.Sql($"""
            INSERT INTO mcp_tool_policies (
                id, tool_name, source, description, is_enabled, max_calls_per_minute, timeout_seconds,
                argument_schema_json, locked, created_at, updated_at
            )
            SELECT
                gen_random_uuid(),
                'request_human_support',
                'server',
                'Escalona o atendimento da IA para um técnico humano abrindo chamado ou comentando no chamado existente com resumo estruturado.',
                TRUE,
                10,
                30,
                '{RequestHumanSupportSchema.Replace("'", "''")}',
                FALSE,
                NOW(),
                NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'request_human_support'
                  AND client_id IS NULL
                  AND site_id IS NULL
                  AND agent_id IS NULL
            );
            """);
    }

    public override void Down()
    {
        if (Schema.Table(AuditTable).Exists())
            Delete.Table(AuditTable);
    }
}
