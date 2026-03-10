using FluentMigrator;

namespace Meduza.Migrations.Migrations;

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

        // Seed padrão: filesystem.read_file habilitada para todos
        Insert.IntoTable("mcp_tool_policies").Row(new
        {
            id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            client_id = (Guid?)null,
            site_id = (Guid?)null,
            agent_id = (Guid?)null,
            tool_name = "filesystem.read_file",
            is_enabled = true,
            argument_schema_json = @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""maxLength"": 500 },
    ""maxBytes"": { ""type"": ""integer"", ""maximum"": 10240 }
  },
  ""required"": [""path""]
}",
            max_calls_per_minute = 5,
            timeout_seconds = 10,
            created_at = DateTime.UtcNow,
            updated_at = (DateTime?)null
        });
    }

    public override void Down()
    {
        Delete.Table("mcp_tool_policies");
    }
}
