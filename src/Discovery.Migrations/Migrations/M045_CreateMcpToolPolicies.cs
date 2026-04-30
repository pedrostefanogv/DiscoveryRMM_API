using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260310_045)]
public class M045_CreateMcpToolPolicies : Migration
{
    public override void Up()
    {
        Create.Table("mcp_tool_policies")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("sites", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("tool_name").AsString(200).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("argument_schema_json").AsString(int.MaxValue).Nullable()
            .WithColumn("max_calls_per_minute").AsInt32().NotNullable().WithDefaultValue(5)
            .WithColumn("timeout_seconds").AsInt32().NotNullable().WithDefaultValue(10)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_mcp_tool_policies_scope")
            .OnTable("mcp_tool_policies")
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("tool_name").Ascending();

        // Seed padrão: filesystem.read_file habilitada para todos (idempotente, GUID gerado pelo banco)
        Execute.Sql(@"
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, is_enabled,
                argument_schema_json, max_calls_per_minute, timeout_seconds,
                created_at, updated_at
            )
            SELECT
                gen_random_uuid(), NULL, NULL, NULL, 'filesystem.read_file', true,
                '{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""maxLength"": 500 },
    ""maxBytes"": { ""type"": ""integer"", ""maximum"": 10240 }
  },
  ""required"": [""path""]
}',
                5, 10, NOW(), NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM mcp_tool_policies
                WHERE tool_name = 'filesystem.read_file'
                  AND client_id IS NULL AND site_id IS NULL AND agent_id IS NULL
            );
        ");
    }

    public override void Down()
    {
        Delete.Table("mcp_tool_policies");
    }
}
