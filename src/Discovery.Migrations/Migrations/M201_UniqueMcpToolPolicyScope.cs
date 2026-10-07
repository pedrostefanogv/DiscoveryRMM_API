using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Consolida a unicidade das policies MCP: cada tool pode ter NO MAXIMO uma
/// linha por escopo exato (global / cliente / site / agente).
///
/// Motivo: o auto-registro das tools do agente e o upsert da tela de settings
/// fazem "ler-depois-inserir"; com dois writers concorrentes era possivel criar
/// linhas duplicadas no mesmo escopo. O indice unico (com COALESCE para tratar
/// NULL como nivel) elimina a corrida no banco.
///
/// Dedupe: em cada grupo duplicado vence a linha MAIS RECENTE
/// (COALESCE(updated_at, created_at)); em empate, a DESABILITADA (mais
/// restritiva). A migration nao restaura o que for removido no Down.
/// </summary>
[Migration(20261120_201)]
public class M201_UniqueMcpToolPolicyScope : Migration
{
    private const string ZeroUuid = "00000000-0000-0000-0000-000000000000";

    public override void Up()
    {
        // 1) Remove duplicatas do mesmo escopo exato.
        Execute.Sql($@"
            WITH ranked AS (
                SELECT id,
                       ROW_NUMBER() OVER (
                           PARTITION BY tool_name,
                               COALESCE(client_id, '{ZeroUuid}'::uuid),
                               COALESCE(site_id, '{ZeroUuid}'::uuid),
                               COALESCE(agent_id, '{ZeroUuid}'::uuid)
                           ORDER BY COALESCE(updated_at, created_at) DESC,
                                    CASE WHEN is_enabled THEN 1 ELSE 0 END ASC,
                                    created_at DESC,
                                    id
                       ) AS rn
                FROM mcp_tool_policies
            )
            DELETE FROM mcp_tool_policies AS target
            USING ranked
            WHERE target.id = ranked.id AND ranked.rn > 1;
        ");

        // 2) Impede novas duplicatas no mesmo escopo.
        Execute.Sql($@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_tool_policies_scope_tool
                ON mcp_tool_policies (
                    tool_name,
                    COALESCE(client_id, '{ZeroUuid}'::uuid),
                    COALESCE(site_id, '{ZeroUuid}'::uuid),
                    COALESCE(agent_id, '{ZeroUuid}'::uuid)
                );
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_mcp_tool_policies_scope_tool;");
    }
}
