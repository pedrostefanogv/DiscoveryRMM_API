using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Garante que exista uma configuração global de servidor com valores padrão.
/// Isso evita ausência de baseline ao resolver configuração efetiva.
/// </summary>
[Migration(20260304_024)]
public class M024_SeedDefaultServerConfiguration : Migration
{
    public override void Up()
    {
        if (ServerConfigurationExists())
            return;

        Insert.IntoTable("server_configurations")
            .Row(new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(),
                ["recovery_enabled"] = false,
                ["discovery_enabled"] = false,
                ["p2p_files_enabled"] = false,
                ["support_enabled"] = false,
                ["knowledge_base_enabled"] = false,
                ["app_store_policy"] = 1,
                ["inventory_interval_hours"] = 24,
                ["auto_update_settings_json"] = string.Empty,
                ["token_expiration_days"] = 365,
                ["max_tokens_per_agent"] = 3,
                ["agent_heartbeat_interval_seconds"] = 60,
                ["agent_offline_threshold_seconds"] = 300,
                ["branding_settings_json"] = string.Empty,
                ["ai_integration_settings_json"] = string.Empty,
                ["locked_fields_json"] = "[]",
                ["created_at"] = DateTime.UtcNow,
                ["updated_at"] = DateTime.UtcNow,
                ["created_by"] = "migration",
                ["updated_by"] = "migration",
                ["version"] = 1,
            });
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM server_configurations;");
    }

    private bool ServerConfigurationExists()
    {
        var exists = false;

        Execute.WithConnection((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(1) FROM server_configurations";

            var result = cmd.ExecuteScalar();
            exists = Convert.ToInt32(result) > 0;
        });

        return exists;
    }
}
