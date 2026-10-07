using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Governança de MCP tools por escopo (server + agent).
///
/// 1) Remove as policies ÓRFÃS: `filesystem.read_file` (seedada habilitada pela
///    M045) e `postgres.query` (seedada desabilitada pela M133) nunca tiveram
///    handler registrado no McpToolExecutor — nunca chegavam ao LLM e só
///    poluíam a tabela e os aliases de tool call.
/// 2) Adiciona `source` para distinguir a origem da tool (servidor x agente) —
///    a UI de settings precisa saber o que está configurando.
/// 3) Adiciona `locked`: quando true, escopos mais específicos NÃO podem
///    sobrescrever a política (mesma semântica de "campos bloqueados para
///    herança" das configurações globais).
/// </summary>
[Migration(20261115_200)]
public class M200_McpToolPolicyGovernance : Migration
{
    public override void Up()
    {
        // ── 1. Policies órfãs ──────────────────────────────────────────────
        Execute.Sql(@"
            DELETE FROM mcp_tool_policies
            WHERE tool_name IN ('filesystem.read_file', 'postgres.query');
        ");

        // ── 2. source ──────────────────────────────────────────────────────
        if (!Schema.Table("mcp_tool_policies").Column("source").Exists())
        {
            Alter.Table("mcp_tool_policies")
                .AddColumn("source").AsString(16).NotNullable().WithDefaultValue("server");
        }

        // ── 3. locked ──────────────────────────────────────────────────────
        if (!Schema.Table("mcp_tool_policies").Column("locked").Exists())
        {
            Alter.Table("mcp_tool_policies")
                .AddColumn("locked").AsBoolean().NotNullable().WithDefaultValue(false);
        }

        // Índice de consulta por escopo + nome (a UI lista por escopo).
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_mcp_tool_policies_source_tool
                ON mcp_tool_policies (source, tool_name);
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_mcp_tool_policies_source_tool;");

        if (Schema.Table("mcp_tool_policies").Column("locked").Exists())
            Delete.Column("locked").FromTable("mcp_tool_policies");

        if (Schema.Table("mcp_tool_policies").Column("source").Exists())
            Delete.Column("source").FromTable("mcp_tool_policies");

        // Recria as policies órfãs no escopo global com o estado original:
        // filesystem.read_file habilitada (M045) e postgres.query desabilitada (M133).
        InsertOrphan("filesystem.read_file", enabled: true);
        InsertOrphan("postgres.query", enabled: false);
    }

    private void InsertOrphan(string toolName, bool enabled)
    {
        Execute.Sql($@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT gen_random_uuid(), NULL, NULL, NULL, '{toolName}', {(enabled ? "true" : "false")},
                NULL, 5, 10, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = '{toolName}'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");
    }
}
