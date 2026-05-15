using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Corrige colunas timestamp que estão sem timezone no banco.
/// 
/// Problema:
///   Algumas colunas foram criadas como "timestamp without time zone" 
///   enquanto o EF Core as mapeia como "timestamptz". Isso causa drift 
///   de horário na serialização JSON e bugs no cálculo de SLA.
///
/// Correção:
///   ALTER COLUMN para "timestamp with time zone" convertendo os dados
///   existentes de America/Sao_Paulo (fuso do servidor) para UTC.
/// </summary>
[Migration(130)]
public class M130_FixTimestampTimezones : Migration
{
    public override void Up()
    {
        // ── tickets.sla_expires_at ──
        // Foi criada como AsDateTime() na M030 sem timezone.
        Execute.Sql("""
            ALTER TABLE tickets
            ALTER COLUMN sla_expires_at TYPE timestamptz
            USING sla_expires_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── ticket_activity_logs.created_at ──
        // Foi criada como AsDateTime() na M030 sem timezone.
        Execute.Sql("""
            ALTER TABLE ticket_activity_logs
            ALTER COLUMN created_at TYPE timestamptz
            USING created_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── departments.created_at / updated_at ──
        // Foram criadas como AsDateTime() (padrão) na M030 sem timezone.
        Execute.Sql("""
            ALTER TABLE departments
            ALTER COLUMN created_at TYPE timestamptz
            USING created_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE departments
            ALTER COLUMN updated_at TYPE timestamptz
            USING updated_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── workflow_profiles.created_at ──
        // Foi criada como AsDateTime() (padrão) na M030 sem timezone.
        Execute.Sql("""
            ALTER TABLE workflow_profiles
            ALTER COLUMN created_at TYPE timestamptz
            USING created_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── auth_audit_logs.occurred_at ──
        // Sem declaração de tipo na migração — padrão sem timezone.
        Execute.Sql("""
            ALTER TABLE auth_audit_logs
            ALTER COLUMN occurred_at TYPE timestamptz
            USING occurred_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── agent_software_inventory ──
        // 5 colunas: collected_at, first_seen_at, last_seen_at, created_at, updated_at
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN collected_at TYPE timestamptz
            USING collected_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN first_seen_at TYPE timestamptz
            USING first_seen_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN last_seen_at TYPE timestamptz
            USING last_seen_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN created_at TYPE timestamptz
            USING created_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN updated_at TYPE timestamptz
            USING updated_at AT TIME ZONE 'America/Sao_Paulo';
        """);

        // ── software_catalog.created_at / updated_at ──
        Execute.Sql("""
            ALTER TABLE software_catalog
            ALTER COLUMN created_at TYPE timestamptz
            USING created_at AT TIME ZONE 'America/Sao_Paulo';
        """);
        Execute.Sql("""
            ALTER TABLE software_catalog
            ALTER COLUMN updated_at TYPE timestamptz
            USING updated_at AT TIME ZONE 'America/Sao_Paulo';
        """);
    }

    public override void Down()
    {
        // Reverter para timestamp without time zone (perigoso – faz a conversão inversa)
        Execute.Sql("""
            ALTER TABLE tickets
            ALTER COLUMN sla_expires_at TYPE timestamp WITHOUT TIME ZONE
            USING sla_expires_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE ticket_activity_logs
            ALTER COLUMN created_at TYPE timestamp WITHOUT TIME ZONE
            USING created_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE departments
            ALTER COLUMN created_at TYPE timestamp WITHOUT TIME ZONE
            USING created_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE departments
            ALTER COLUMN updated_at TYPE timestamp WITHOUT TIME ZONE
            USING updated_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE workflow_profiles
            ALTER COLUMN created_at TYPE timestamp WITHOUT TIME ZONE
            USING created_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE auth_audit_logs
            ALTER COLUMN occurred_at TYPE timestamp WITHOUT TIME ZONE
            USING occurred_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN collected_at TYPE timestamp WITHOUT TIME ZONE
            USING collected_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN first_seen_at TYPE timestamp WITHOUT TIME ZONE
            USING first_seen_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN last_seen_at TYPE timestamp WITHOUT TIME ZONE
            USING last_seen_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN created_at TYPE timestamp WITHOUT TIME ZONE
            USING created_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE agent_software_inventory
            ALTER COLUMN updated_at TYPE timestamp WITHOUT TIME ZONE
            USING updated_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE software_catalog
            ALTER COLUMN created_at TYPE timestamp WITHOUT TIME ZONE
            USING created_at AT TIME ZONE 'UTC';
        """);
        Execute.Sql("""
            ALTER TABLE software_catalog
            ALTER COLUMN updated_at TYPE timestamp WITHOUT TIME ZONE
            USING updated_at AT TIME ZONE 'UTC';
        """);
    }
}
