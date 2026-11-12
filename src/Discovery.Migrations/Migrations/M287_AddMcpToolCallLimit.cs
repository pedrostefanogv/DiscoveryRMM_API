using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Padronizacao do rate limit das MCP tools do chat de IA (20 chamadas/min por
/// tool) e criacao do TETO GLOBAL herdavel de chamadas de tools por minuto.
///
/// Antes: o auto-registro gravava 5/min para tools do servidor e 10/min para
/// tools do agente — dois numeros que nao correspondiam a nenhuma decisao de
/// produto. Agora o padrao e UNICO (20/min) e o que reduz o consumo por escopo e
/// o teto global, com a mesma semantica de heranca das demais features
/// (M283 KnowledgeEmbeddingEnabled / M286 kill switches / UpdatesTabEnabled):
///
///   server_configurations.mcp_tool_calls_per_minute -> NOT NULL DEFAULT 20
///     (default global IDENTICO ao padrao do produto: zero surpresa no rollout);
///   client_configurations.mcp_tool_calls_per_minute -> NULLABLE (null = herda servidor);
///   site_configurations.mcp_tool_calls_per_minute   -> NULLABLE (null = herda cliente/servidor).
///
/// Precedencia efetiva: site ?? client ?? server, sanitizada em 1..600.
///
/// LIMITE EFETIVO DE UMA TOOL = min(max_calls_per_minute da politica da tool,
/// teto global do escopo) — o teto global e um PISO, nunca um aumento. 0 (zero)
/// nao e aceito pela validacao da API: o rate limit nao e desligavel por aqui.
///
/// Nao ha UPDATE/backfill retroativo de proposito: o DEFAULT 20 do servidor ja
/// preserva a frota existente (e sobe o antigo 5/10 para o padrao novo) sem
/// tocar nas linhas de client/site, que continuam herdando.
/// </summary>
[Migration(20270120_287)]
public class M287_AddMcpToolCallLimit : Migration
{
    private const int DefaultCallsPerMinute = 20;

    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists()
            && !Schema.Table("server_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("mcp_tool_calls_per_minute").AsInt32().NotNullable().WithDefaultValue(DefaultCallsPerMinute);
        }

        if (Schema.Table("client_configurations").Exists()
            && !Schema.Table("client_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("mcp_tool_calls_per_minute").AsInt32().Nullable();
        }

        if (Schema.Table("site_configurations").Exists()
            && !Schema.Table("site_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("mcp_tool_calls_per_minute").AsInt32().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Exists()
            && Schema.Table("site_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Delete.Column("mcp_tool_calls_per_minute").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Delete.Column("mcp_tool_calls_per_minute").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Exists()
            && Schema.Table("server_configurations").Column("mcp_tool_calls_per_minute").Exists())
        {
            Delete.Column("mcp_tool_calls_per_minute").FromTable("server_configurations");
        }
    }
}
