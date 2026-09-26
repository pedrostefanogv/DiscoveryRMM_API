using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seed da policy da MCP tool "knowledge_list" — catálogo de artigos PUBLICADOS
/// da base de conhecimento. Complementa knowledge_search: perguntas de catálogo
/// ("quais artigos existem?") não têm query e antes faziam o LLM concluir que a
/// base estava vazia. Idempotente (NOT EXISTS no escopo global).
/// </summary>
[Migration(20260927_169)]
public class M169_SeedKnowledgeListToolPolicy : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'knowledge_list', true,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""Filtra por categoria exata do artigo (opcional)"" },
    ""limit"": { ""type"": ""integer"", ""default"": 50, ""minimum"": 1, ""maximum"": 100 }
  },
  ""required"": []
}',
                10, 10, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'knowledge_list'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM mcp_tool_policies WHERE tool_name = 'knowledge_list' AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;");
    }
}
