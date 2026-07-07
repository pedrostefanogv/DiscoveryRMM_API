using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seed das MCP tools adicionais (time.current, sequential_thinking, memory.search, postgres.query)
/// referenciadas pelo McpToolExecutor genérico.
/// </summary>
[Migration(20260707_133)]
public class M133_SeedMcpToolPolicies : Migration
{
    public override void Up()
    {
        // ── time.current ──
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'time.current', true,
                NULL,
                30, 5, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'time.current'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");

        // ── sequential_thinking ──
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'sequential_thinking', true,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""thought"": { ""type"": ""string"", ""description"": ""Pensamento ou análise atual"" },
    ""thought_number"": { ""type"": ""integer"", ""minimum"": 1 },
    ""total_thoughts"": { ""type"": ""integer"", ""minimum"": 1 },
    ""next_thought_needed"": { ""type"": ""boolean"" },
    ""is_revision"": { ""type"": ""boolean"" },
    ""revises_thought"": { ""type"": ""integer"" },
    ""branch_from_thought"": { ""type"": ""integer"" },
    ""branch_id"": { ""type"": ""string"" },
    ""needs_more_thoughts"": { ""type"": ""boolean"" }
  },
  ""required"": [""thought"", ""thought_number"", ""total_thoughts"", ""next_thought_needed""]
}',
                20, 15, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'sequential_thinking'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");

        // ── memory.search ──
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'memory.search', true,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""Termos de busca na memória"" },
    ""max_results"": { ""type"": ""integer"", ""default"": 5 }
  },
  ""required"": [""query""]
}',
                10, 5, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'memory.search'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");

        // ── postgres.query (read-only, desabilitada por padrão) ──
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'postgres.query', false,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""Query SQL SELECT read-only. Apenas SELECT é permitido. Máximo 100 linhas."" }
  },
  ""required"": [""query""]
}',
                3, 15, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'postgres.query'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");

        // ── knowledge_search (garantir seed se não existir) ──
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'knowledge_search', true,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""Termos de busca na base de conhecimento"" },
    ""max_results"": { ""type"": ""integer"", ""default"": 3, ""minimum"": 1, ""maximum"": 5 }
  },
  ""required"": [""query""]
}',
                10, 10, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'knowledge_search'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM mcp_tool_policies WHERE tool_name IN ('time.current', 'sequential_thinking', 'memory.search', 'postgres.query', 'knowledge_search') AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL;");
    }
}
