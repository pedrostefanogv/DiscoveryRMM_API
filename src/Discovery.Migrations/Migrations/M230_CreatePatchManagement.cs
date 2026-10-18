using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Gerenciador de Patches do Windows: inventário de KBs (pendentes/instaladas),
/// aprovações por escopo, políticas, auditoria e estado de enforcement no SO.
///
/// Aditiva e idempotente (CREATE TABLE IF NOT EXISTS / ADD COLUMN IF NOT EXISTS),
/// mesmo padrão das migrações recentes. Também cria as colunas de reinício
/// pendente em agents e os kill switches de configuração.
/// </summary>
[Migration(20261220_230)]
public class M230_CreatePatchManagement : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS agent_windows_updates (
                id uuid PRIMARY KEY,
                agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                update_key varchar(120) NOT NULL,
                group_key varchar(120) NOT NULL,
                kb_ids jsonb,
                update_id varchar(64),
                revision int,
                title varchar(500) NOT NULL DEFAULT '',
                description varchar(2000),
                classification varchar(80),
                categories jsonb,
                severity varchar(40),
                is_pending boolean NOT NULL DEFAULT false,
                is_installed boolean NOT NULL DEFAULT false,
                is_hidden boolean NOT NULL DEFAULT false,
                is_downloaded boolean NOT NULL DEFAULT false,
                is_mandatory boolean NOT NULL DEFAULT false,
                requires_reboot boolean NOT NULL DEFAULT false,
                max_download_size bigint,
                support_url varchar(1000),
                published_at timestamptz,
                last_deployed_at timestamptz,
                installed_at timestamptz,
                reported_at timestamptz NOT NULL DEFAULT now(),
                install_started_at timestamptz,
                install_result_code int,
                install_error varchar(1000),
                approval_state smallint NOT NULL DEFAULT 0,
                approval_source varchar(30),
                status smallint NOT NULL DEFAULT 0,
                source varchar(20),
                raw jsonb,
                first_seen_at timestamptz NOT NULL DEFAULT now(),
                last_seen_at timestamptz NOT NULL DEFAULT now(),
                is_present boolean NOT NULL DEFAULT true,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
        ");

        Execute.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_windows_updates_agent_update
                ON agent_windows_updates(agent_id, update_key);
            CREATE INDEX IF NOT EXISTS ix_agent_windows_updates_agent_pending
                ON agent_windows_updates(agent_id, is_pending, is_present);
            CREATE INDEX IF NOT EXISTS ix_agent_windows_updates_group_key
                ON agent_windows_updates(group_key);
            CREATE INDEX IF NOT EXISTS ix_agent_windows_updates_agent_group
                ON agent_windows_updates(agent_id, group_key);
            CREATE INDEX IF NOT EXISTS ix_agent_windows_updates_approval_state
                ON agent_windows_updates(approval_state);
            CREATE INDEX IF NOT EXISTS ix_agent_windows_updates_status
                ON agent_windows_updates(status);
        ");

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS agent_windows_update_snapshots (
                agent_id uuid PRIMARY KEY REFERENCES agents(id) ON DELETE CASCADE,
                collected_at timestamptz,
                scan_status varchar(20) NOT NULL DEFAULT 'ok',
                scan_duration_ms int,
                scan_error varchar(1000),
                pending_count int NOT NULL DEFAULT 0,
                installed_count int NOT NULL DEFAULT 0,
                denied_count int NOT NULL DEFAULT 0,
                pending_reboot boolean NOT NULL DEFAULT false,
                pending_reboot_reasons jsonb,
                reboot_checked_at timestamptz,
                updated_at timestamptz
            );
        ");

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS patch_approvals (
                id uuid PRIMARY KEY,
                scope_type smallint NOT NULL DEFAULT 0,
                scope_id uuid,
                client_id uuid,
                site_id uuid,
                agent_id uuid,
                update_key varchar(120) NOT NULL,
                kb_id varchar(20),
                action smallint NOT NULL DEFAULT 0,
                reason varchar(500),
                created_by varchar(200),
                updated_by varchar(200),
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_patch_approvals_scope_update
                ON patch_approvals(scope_type, scope_id, update_key);
            -- Postgres trata NULLs como distintos em UNIQUE: sem estes índices
            -- parciais, duas regras GLOBAIS para a mesma KB poderiam coexistir.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_patch_approvals_global_update
                ON patch_approvals(update_key) WHERE scope_id IS NULL;
            CREATE INDEX IF NOT EXISTS ix_patch_approvals_update_key
                ON patch_approvals(update_key);
        ");

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS patch_approval_audits (
                id uuid PRIMARY KEY,
                rule_id uuid,
                change_type smallint NOT NULL DEFAULT 0,
                scope_type smallint NOT NULL DEFAULT 0,
                scope_id uuid,
                client_id uuid,
                site_id uuid,
                agent_id uuid,
                update_key varchar(120) NOT NULL DEFAULT '',
                kb_id varchar(20),
                old_action smallint,
                new_action smallint,
                reason varchar(500),
                changed_by varchar(200),
                ip_address varchar(64),
                changed_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_patch_approval_audits_changed_at
                ON patch_approval_audits(changed_at);
            CREATE INDEX IF NOT EXISTS ix_patch_approval_audits_update_key
                ON patch_approval_audits(update_key);
        ");

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS patch_policies (
                id uuid PRIMARY KEY,
                scope_type smallint NOT NULL DEFAULT 0,
                scope_id uuid,
                client_id uuid,
                site_id uuid,
                enforcement_mode smallint NOT NULL DEFAULT 0,
                auto_approve_classifications jsonb,
                auto_approve_severities jsonb,
                auto_install_enabled boolean NOT NULL DEFAULT false,
                maintenance_window_json jsonb,
                reboot_policy smallint NOT NULL DEFAULT 0,
                reboot_allowed boolean NOT NULL DEFAULT false,
                max_installs_per_run int NOT NULL DEFAULT 50,
                hide_denied_updates boolean NOT NULL DEFAULT false,
                updated_by varchar(200),
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_patch_policies_scope
                ON patch_policies(scope_type, scope_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_patch_policies_global_scope
                ON patch_policies(scope_type) WHERE scope_id IS NULL;
        ");

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS patch_os_states (
                id uuid PRIMARY KEY,
                agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
                command_id uuid,
                applied_by varchar(200),
                applied_at timestamptz NOT NULL DEFAULT now(),
                previous_json jsonb,
                applied_json jsonb,
                reverted_at timestamptz,
                reverted_by varchar(200)
            );
            CREATE INDEX IF NOT EXISTS ix_patch_os_states_agent
                ON patch_os_states(agent_id);
        ");

        // Reinício pendente no agente (badge em /agents e detalhe).
        Execute.Sql(@"
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS pending_reboot boolean NOT NULL DEFAULT false;
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS pending_reboot_reasons text;
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS pending_reboot_checked_at timestamptz;
        ");

        // Kill switch do módulo (default habilitado).
        Execute.Sql(@"
            ALTER TABLE server_configurations
                ADD COLUMN IF NOT EXISTS windows_update_enabled boolean NOT NULL DEFAULT true;
            ALTER TABLE server_configurations
                ADD COLUMN IF NOT EXISTS windows_update_scan_interval_minutes int NOT NULL DEFAULT 360;
            ALTER TABLE client_configurations
                ADD COLUMN IF NOT EXISTS windows_update_enabled boolean;
            ALTER TABLE client_configurations
                ADD COLUMN IF NOT EXISTS windows_update_scan_interval_minutes int;
            ALTER TABLE site_configurations
                ADD COLUMN IF NOT EXISTS windows_update_enabled boolean;
            ALTER TABLE site_configurations
                ADD COLUMN IF NOT EXISTS windows_update_scan_interval_minutes int;
        ");
    }

    public override void Down()
    {
        Execute.Sql(@"
            DROP TABLE IF EXISTS patch_os_states;
            DROP TABLE IF EXISTS patch_policies;
            DROP TABLE IF EXISTS patch_approval_audits;
            DROP TABLE IF EXISTS patch_approvals;
            DROP TABLE IF EXISTS agent_windows_update_snapshots;
            DROP TABLE IF EXISTS agent_windows_updates;
        ");

        Execute.Sql(@"
            ALTER TABLE agents DROP COLUMN IF EXISTS pending_reboot;
            ALTER TABLE agents DROP COLUMN IF EXISTS pending_reboot_reasons;
            ALTER TABLE agents DROP COLUMN IF EXISTS pending_reboot_checked_at;
        ");

        Execute.Sql(@"
            ALTER TABLE server_configurations DROP COLUMN IF EXISTS windows_update_enabled;
            ALTER TABLE server_configurations DROP COLUMN IF EXISTS windows_update_scan_interval_minutes;
            ALTER TABLE client_configurations DROP COLUMN IF EXISTS windows_update_enabled;
            ALTER TABLE client_configurations DROP COLUMN IF EXISTS windows_update_scan_interval_minutes;
            ALTER TABLE site_configurations DROP COLUMN IF EXISTS windows_update_enabled;
            ALTER TABLE site_configurations DROP COLUMN IF EXISTS windows_update_scan_interval_minutes;
        ");
    }
}
