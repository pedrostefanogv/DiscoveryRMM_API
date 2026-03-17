using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260317_082)]
public class M082_AgentTokenIndefiniteAndConfigCleanup : Migration
{
    public override void Up()
    {
        // Permite token sem expiracao para agents.
        Alter.Table("agent_tokens")
            .AlterColumn("expires_at")
            .AsCustom("timestamptz")
            .Nullable();

        // Garante no maximo um token ativo por agent, revogando excedentes antigos antes do indice unico.
        Execute.Sql("""
            WITH ranked_active AS (
                SELECT id,
                       agent_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY agent_id
                           ORDER BY created_at DESC, id DESC
                       ) AS rn
                FROM agent_tokens
                WHERE revoked_at IS NULL
            )
            UPDATE agent_tokens t
            SET revoked_at = NOW()
            FROM ranked_active ra
            WHERE t.id = ra.id
              AND ra.rn > 1;
            """);

        Execute.Sql("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_tokens_active_agent
                ON agent_tokens (agent_id)
                WHERE revoked_at IS NULL;
            """);

        if (Schema.Table("server_configurations").Column("token_expiration_days").Exists())
            Delete.Column("token_expiration_days").FromTable("server_configurations");

        if (Schema.Table("server_configurations").Column("max_tokens_per_agent").Exists())
            Delete.Column("max_tokens_per_agent").FromTable("server_configurations");

        if (Schema.Table("server_configurations").Column("agent_offline_threshold_seconds").Exists())
            Delete.Column("agent_offline_threshold_seconds").FromTable("server_configurations");

        if (Schema.Table("client_configurations").Column("token_expiration_days").Exists())
            Delete.Column("token_expiration_days").FromTable("client_configurations");

        if (Schema.Table("client_configurations").Column("max_tokens_per_agent").Exists())
            Delete.Column("max_tokens_per_agent").FromTable("client_configurations");

        if (Schema.Table("client_configurations").Column("agent_offline_threshold_seconds").Exists())
            Delete.Column("agent_offline_threshold_seconds").FromTable("client_configurations");
    }

    public override void Down()
    {
        if (!Schema.Table("server_configurations").Column("token_expiration_days").Exists())
            Alter.Table("server_configurations")
                .AddColumn("token_expiration_days")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(365);

        if (!Schema.Table("server_configurations").Column("max_tokens_per_agent").Exists())
            Alter.Table("server_configurations")
                .AddColumn("max_tokens_per_agent")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(3);

        if (!Schema.Table("server_configurations").Column("agent_offline_threshold_seconds").Exists())
            Alter.Table("server_configurations")
                .AddColumn("agent_offline_threshold_seconds")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(300);

        if (!Schema.Table("client_configurations").Column("token_expiration_days").Exists())
            Alter.Table("client_configurations")
                .AddColumn("token_expiration_days")
                .AsInt32()
                .Nullable();

        if (!Schema.Table("client_configurations").Column("max_tokens_per_agent").Exists())
            Alter.Table("client_configurations")
                .AddColumn("max_tokens_per_agent")
                .AsInt32()
                .Nullable();

        if (!Schema.Table("client_configurations").Column("agent_offline_threshold_seconds").Exists())
            Alter.Table("client_configurations")
                .AddColumn("agent_offline_threshold_seconds")
                .AsInt32()
                .Nullable();

        Execute.Sql("DROP INDEX IF EXISTS ux_agent_tokens_active_agent;");

        Execute.Sql("""
            UPDATE agent_tokens
            SET expires_at = COALESCE(expires_at, NOW() + INTERVAL '365 days')
            WHERE expires_at IS NULL;
            """);

        Alter.Table("agent_tokens")
            .AlterColumn("expires_at")
            .AsCustom("timestamptz")
            .NotNullable();
    }
}
