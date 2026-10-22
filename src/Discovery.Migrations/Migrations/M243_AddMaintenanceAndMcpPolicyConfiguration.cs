using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Configurações em cascata (servidor → cliente → site) para o agent:
///  - maintenance_window_json: janela de manutenção (reboot/patches) —
///    JSON { enabled, daysOfWeek, startUtc, durationMinutes, rebootGraceSeconds }.
///  - mcp_tool_policy_json: allowlist de tools MCP do chat —
///    JSON { mode: "all"|"allowlist", allow: [...] }.
/// Null/vazio = herda do nível superior; default efetivo (servidor vazio) =
/// janela desligada e mode "all" (compatibilidade total com a frota atual —
/// o agent aplica os defaults quando o campo vem ausente).
/// </summary>
[Migration(20261222_243)]
public class M243_AddMaintenanceAndMcpPolicyConfiguration : Migration
{
    private const string ServerTable = "server_configurations";
    private const string ClientTable = "client_configurations";
    private const string SiteTable = "site_configurations";

    public override void Up()
    {
        foreach (var table in new[] { ServerTable, ClientTable, SiteTable })
        {
            if (Schema.Table(table).Exists())
            {
                AddNullableColumnIfMissing(table, "maintenance_window_json", "jsonb");
                AddNullableColumnIfMissing(table, "mcp_tool_policy_json", "jsonb");
            }
        }
    }

    public override void Down()
    {
        foreach (var table in new[] { ServerTable, ClientTable, SiteTable })
        {
            if (Schema.Table(table).Exists())
            {
                if (Schema.Table(table).Column("maintenance_window_json").Exists())
                    Delete.Column("maintenance_window_json").FromTable(table);
                if (Schema.Table(table).Column("mcp_tool_policy_json").Exists())
                    Delete.Column("mcp_tool_policy_json").FromTable(table);
            }
        }
    }

    private void AddNullableColumnIfMissing(string table, string column, string sqlType)
    {
        if (!Schema.Table(table).Column(column).Exists())
            Alter.Table(table).AddColumn(column).AsCustom(sqlType).Nullable();
    }
}
