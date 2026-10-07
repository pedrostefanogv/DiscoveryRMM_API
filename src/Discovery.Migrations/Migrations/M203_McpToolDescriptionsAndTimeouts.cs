using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Catálogo de MCP tools: descrição por tool + timeouts padrão mais realistas.
///
/// 1) mcp_tool_policies.description — o agente já registra nome/descrição/schema
///    das suas tools no startup; sem persistir a descrição, a tela
///    /settings/mcp-tools mostrava "Ferramenta do agente executada na máquina do
///    cliente (&lt;nome&gt;)" para TODAS as ~50 tools do agente (nenhuma explicação de
///    o que faz ou quando usar).
/// 2) Timeouts: os seeds do servidor nasceram com valores baixos para operações
///    com I/O (knowledge_search 10s, memory.search 5s). O UPDATE abaixo só toca
///    linhas GLOBAIS que ainda estão exatamente no valor do seed — quem ajustou
///    a política não é afetado.
/// </summary>
[Migration(20261122_203)]
public class M203_McpToolDescriptionsAndTimeouts : Migration
{
    public override void Up()
    {
        // 4000 (e não 2000): a descrição enriquecida de create_ticket tem ~2.3k
        // caracteres (EnrichAgentToolDescription + texto do agente). Com 2000 o
        // SaveChanges do auto-registro estourava e derrubava TODO o lote de
        // descrições (EF persiste o lote em uma transação).
        if (!Schema.Table("mcp_tool_policies").Column("description").Exists())
        {
            Alter.Table("mcp_tool_policies")
                .AddColumn("description").AsString(4000).Nullable();
        }

        // knowledge_search: busca na base com embedding/IO — 10s cortava buscas
        // legítimas. 30s mantém o teto curto para o turno não travar.
        Execute.Sql(@"
            UPDATE mcp_tool_policies
               SET timeout_seconds = 30, updated_at = NOW()
             WHERE tool_name = 'knowledge_search'
               AND timeout_seconds = 10
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;
        ");

        // memory.search: consulta ILIKE sobre o histórico de conversas — 5s é
        // apertado demais em bases grandes.
        Execute.Sql(@"
            UPDATE mcp_tool_policies
               SET timeout_seconds = 20, updated_at = NOW()
             WHERE tool_name = 'memory.search'
               AND timeout_seconds = 5
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;
        ");
    }

    public override void Down()
    {
        Execute.Sql(@"
            UPDATE mcp_tool_policies
               SET timeout_seconds = 10, updated_at = NOW()
             WHERE tool_name = 'knowledge_search'
               AND timeout_seconds = 30
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;
        ");

        Execute.Sql(@"
            UPDATE mcp_tool_policies
               SET timeout_seconds = 5, updated_at = NOW()
             WHERE tool_name = 'memory.search'
               AND timeout_seconds = 20
               AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;
        ");

        if (Schema.Table("mcp_tool_policies").Column("description").Exists())
            Delete.Column("description").FromTable("mcp_tool_policies");
    }
}
