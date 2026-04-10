using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Consolida tenant_settings em client_configurations e remove a tabela tenant_settings.
///
/// Estratégia alinhada com Data Operations do FluentMigrator:
/// - Escrita via API fluente (Insert/Update/Delete, incluindo Dictionary)
/// - Leitura mínima via Execute.WithConnection para suportar copy/upsert entre tabelas
/// - Sem Execute.Sql para DML em massa
/// </summary>
[Migration(20260304_020)]
public class M020_ConsolidateTenantSettings : Migration
{
    public override void Up()
    {
        var tenantRows = ReadTenantSettingsRows();

        foreach (var row in tenantRows)
        {
            if (ClientConfigurationExists(row.ClientId))
            {
                Update.Table("client_configurations")
                    .Set(new Dictionary<string, object>
                    {
                        ["token_expiration_days"] = row.TokenExpirationDays,
                        ["max_tokens_per_agent"] = row.MaxTokensPerAgent,
                        ["agent_heartbeat_interval_seconds"] = row.AgentHeartbeatIntervalSeconds,
                        ["agent_offline_threshold_seconds"] = row.AgentOfflineThresholdSeconds,
                        ["updated_at"] = row.UpdatedAt
                    })
                    .Where(new Dictionary<string, object>
                    {
                        ["client_id"] = row.ClientId
                    });
            }
            else
            {
                Insert.IntoTable("client_configurations")
                    .Row(new Dictionary<string, object>
                    {
                        ["id"] = row.Id,
                        ["client_id"] = row.ClientId,
                        ["token_expiration_days"] = row.TokenExpirationDays,
                        ["max_tokens_per_agent"] = row.MaxTokensPerAgent,
                        ["agent_heartbeat_interval_seconds"] = row.AgentHeartbeatIntervalSeconds,
                        ["agent_offline_threshold_seconds"] = row.AgentOfflineThresholdSeconds,
                        ["created_at"] = row.CreatedAt,
                        ["updated_at"] = row.UpdatedAt,
                        ["version"] = 1
                    });
            }
        }

        Delete.Table("tenant_settings");
    }

    public override void Down()
    {
        Create.Table("tenant_settings")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable().Unique()
                .ForeignKey("fk_tenant_settings_client", "clients", "id")
            .WithColumn("token_expiration_days").AsInt32().NotNullable().WithDefaultValue(365)
            .WithColumn("max_tokens_per_agent").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("agent_heartbeat_interval_seconds").AsInt32().NotNullable().WithDefaultValue(60)
            .WithColumn("agent_offline_threshold_seconds").AsInt32().NotNullable().WithDefaultValue(300)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        var clientRows = ReadClientConfigurationTokenRows();

        foreach (var row in clientRows)
        {
            Insert.IntoTable("tenant_settings")
                .Row(new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid(),
                    ["client_id"] = row.ClientId,
                    ["token_expiration_days"] = row.TokenExpirationDays,
                    ["max_tokens_per_agent"] = row.MaxTokensPerAgent,
                    ["agent_heartbeat_interval_seconds"] = row.AgentHeartbeatIntervalSeconds,
                    ["agent_offline_threshold_seconds"] = row.AgentOfflineThresholdSeconds,
                    ["created_at"] = row.CreatedAt,
                    ["updated_at"] = row.UpdatedAt
                });
        }
    }

    private sealed class TenantSettingsRow
    {
        public Guid Id { get; set; }
        public Guid ClientId { get; set; }
        public int TokenExpirationDays { get; set; }
        public int MaxTokensPerAgent { get; set; }
        public int AgentHeartbeatIntervalSeconds { get; set; }
        public int AgentOfflineThresholdSeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class ClientTokenSettingsRow
    {
        public Guid ClientId { get; set; }
        public int TokenExpirationDays { get; set; }
        public int MaxTokensPerAgent { get; set; }
        public int AgentHeartbeatIntervalSeconds { get; set; }
        public int AgentOfflineThresholdSeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private List<TenantSettingsRow> ReadTenantSettingsRows()
    {
        var rows = new List<TenantSettingsRow>();

        Execute.WithConnection((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT id, client_id, token_expiration_days, max_tokens_per_agent,
                       agent_heartbeat_interval_seconds, agent_offline_threshold_seconds,
                       created_at, updated_at
                FROM tenant_settings";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TenantSettingsRow
                {
                    Id = reader.GetGuid(0),
                    ClientId = reader.GetGuid(1),
                    TokenExpirationDays = reader.GetInt32(2),
                    MaxTokensPerAgent = reader.GetInt32(3),
                    AgentHeartbeatIntervalSeconds = reader.GetInt32(4),
                    AgentOfflineThresholdSeconds = reader.GetInt32(5),
                    CreatedAt = reader.GetDateTime(6),
                    UpdatedAt = reader.GetDateTime(7)
                });
            }
        });

        return rows;
    }

    private List<ClientTokenSettingsRow> ReadClientConfigurationTokenRows()
    {
        var rows = new List<ClientTokenSettingsRow>();

        Execute.WithConnection((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT client_id,
                       COALESCE(token_expiration_days, 365),
                       COALESCE(max_tokens_per_agent, 3),
                       COALESCE(agent_heartbeat_interval_seconds, 60),
                       COALESCE(agent_offline_threshold_seconds, 300),
                       created_at,
                       updated_at
                FROM client_configurations
                WHERE token_expiration_days IS NOT NULL
                   OR max_tokens_per_agent IS NOT NULL
                   OR agent_heartbeat_interval_seconds IS NOT NULL
                   OR agent_offline_threshold_seconds IS NOT NULL";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ClientTokenSettingsRow
                {
                    ClientId = reader.GetGuid(0),
                    TokenExpirationDays = reader.GetInt32(1),
                    MaxTokensPerAgent = reader.GetInt32(2),
                    AgentHeartbeatIntervalSeconds = reader.GetInt32(3),
                    AgentOfflineThresholdSeconds = reader.GetInt32(4),
                    CreatedAt = reader.GetDateTime(5),
                    UpdatedAt = reader.GetDateTime(6)
                });
            }
        });

        return rows;
    }

    private bool ClientConfigurationExists(Guid clientId)
    {
        var exists = false;

        Execute.WithConnection((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(1) FROM client_configurations WHERE client_id = @client_id";

            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@client_id";
            parameter.Value = clientId;
            cmd.Parameters.Add(parameter);

            var result = cmd.ExecuteScalar();
            exists = Convert.ToInt32(result) > 0;
        });

        return exists;
    }
}
