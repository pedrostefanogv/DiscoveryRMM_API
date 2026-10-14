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
///
/// IMPORTANTE (versão): este arquivo usava [Migration(130)], enquanto as outras
/// 229 migrations usam o padrão data (ex.: [Migration(20260301_001)]). Como 130
/// é o menor valor, o FluentMigrator executava ESTA migration primeiro em um
/// banco novo, antes de M006 (que cria "tickets") — o ALTER TABLE falhava com
/// "42P01: relation does not exist" e a API abortava no startup (crash-loop).
/// A versão agora segue o padrão data e roda DEPOIS das demais, e os ALTERs são
/// idempotentes: só convertem colunas que existem e que ainda estão sem
/// timezone, para não reconverter (e corromper) bancos onde a versão 130 já
/// havia sido aplicada.
/// </summary>
[Migration(20261215_130)]
public class M130_FixTimestampTimezones : Migration
{
    public override void Up()
    {
        // ── tickets.sla_expires_at ── (criada como AsDateTime() na M030)
        ConvertToTimestamptz("tickets", "sla_expires_at");

        // ── ticket_activity_logs.created_at ── (criada como AsDateTime() na M030)
        ConvertToTimestamptz("ticket_activity_logs", "created_at");

        // ── departments.created_at / updated_at ──
        ConvertToTimestamptz("departments", "created_at");
        ConvertToTimestamptz("departments", "updated_at");

        // ── workflow_profiles.created_at ──
        ConvertToTimestamptz("workflow_profiles", "created_at");

        // ── auth_audit_logs.occurred_at ──
        ConvertToTimestamptz("auth_audit_logs", "occurred_at");

        // ── agent_software_inventory (5 colunas) ──
        ConvertToTimestamptz("agent_software_inventory", "collected_at");
        ConvertToTimestamptz("agent_software_inventory", "first_seen_at");
        ConvertToTimestamptz("agent_software_inventory", "last_seen_at");
        ConvertToTimestamptz("agent_software_inventory", "created_at");
        ConvertToTimestamptz("agent_software_inventory", "updated_at");

        // ── software_catalog.created_at / updated_at ──
        ConvertToTimestamptz("software_catalog", "created_at");
        ConvertToTimestamptz("software_catalog", "updated_at");
    }

    public override void Down()
    {
        ConvertToStringWithoutTimeZone("tickets", "sla_expires_at");
        ConvertToStringWithoutTimeZone("ticket_activity_logs", "created_at");
        ConvertToStringWithoutTimeZone("departments", "created_at");
        ConvertToStringWithoutTimeZone("departments", "updated_at");
        ConvertToStringWithoutTimeZone("workflow_profiles", "created_at");
        ConvertToStringWithoutTimeZone("auth_audit_logs", "occurred_at");
        ConvertToStringWithoutTimeZone("agent_software_inventory", "collected_at");
        ConvertToStringWithoutTimeZone("agent_software_inventory", "first_seen_at");
        ConvertToStringWithoutTimeZone("agent_software_inventory", "last_seen_at");
        ConvertToStringWithoutTimeZone("agent_software_inventory", "created_at");
        ConvertToStringWithoutTimeZone("agent_software_inventory", "updated_at");
        ConvertToStringWithoutTimeZone("software_catalog", "created_at");
        ConvertToStringWithoutTimeZone("software_catalog", "updated_at");
    }

    /// <summary>
    /// Converte a coluna para timestamptz SOMENTE se a tabela/coluna existir e
    /// ainda estiver como "timestamp without time zone". Assim a migration pode
    /// rodar de novo (banco antigo que já tinha a versão 130 gravada) sem
    /// converter os dados uma segunda vez.
    /// </summary>
    private void ConvertToTimestamptz(string table, string column)
    {
        Execute.Sql($"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = '{table}'
                      AND column_name = '{column}'
                      AND data_type = 'timestamp without time zone'
                ) THEN
                    ALTER TABLE "{table}"
                        ALTER COLUMN "{column}" TYPE timestamptz
                        USING "{column}" AT TIME ZONE 'America/Sao_Paulo';
                END IF;
            END $$;
            """);
    }

    /// <summary>
    /// Reversão idempotente: só volta para "timestamp without time zone" se a
    /// coluna existir e estiver como timestamptz.
    /// </summary>
    private void ConvertToStringWithoutTimeZone(string table, string column)
    {
        Execute.Sql($"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = '{table}'
                      AND column_name = '{column}'
                      AND data_type = 'timestamp with time zone'
                ) THEN
                    ALTER TABLE "{table}"
                        ALTER COLUMN "{column}" TYPE timestamp WITHOUT TIME ZONE
                        USING "{column}" AT TIME ZONE 'UTC';
                END IF;
            END $$;
            """);
    }
}
