using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Kill switches de FEATURE configuráveis pela UI (CFG-A, grupo A da
/// AUDITORIA_APPSETTINGS_VS_UI). Antes só existiam no appsettings.json; agora a
/// fonte de verdade é a configuração do servidor, com o appsettings como
/// bootstrap/fallback.
///
/// DEFAULT IDÊNTICO AO DE HOJE (zero surpresa no rollout):
///   department_auth_shadow_mode                     -> true
///   push_enabled                                    -> true
///   winget_catalog_enabled                          -> true
///   remote_access_enabled                           -> true
///   remote_access_recording_enabled                 -> true
///   remote_access_recording_auto_delete_expired     -> true
///   automatic_logging_enabled                       -> true
///   open_telemetry_enabled                          -> false
///   migrations_run_on_startup                       -> true
///   realtime_contract_mode                          -> 'auto'
///
/// Semantica por nivel:
///   server_configurations.*          -> NOT NULL DEFAULT (valor de hoje);
///   client/site_configurations.*     -> NULLABLE (null = herda), APENAS para os
///     7 campos herdáveis. OpenTelemetryEnabled, MigrationsRunOnStartup e
///     RealtimeContractMode são do SERVIDOR INTEIRO (pipeline de telemetria do
///     processo, schema do banco e contrato global do dashboard) e não têm coluna
///     em client/site de propósito.
///
/// Nao ha backfill retroativo: o DEFAULT do servidor já preserva a frota atual.
/// </summary>
[Migration(20270118_286)]
public class M286_AddFeatureKillSwitches : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists())
        {
            if (!Schema.Table("server_configurations").Column("department_auth_shadow_mode").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("department_auth_shadow_mode").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("push_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("push_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("winget_catalog_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("winget_catalog_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("remote_access_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("remote_access_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("remote_access_recording_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("remote_access_recording_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("remote_access_recording_auto_delete_expired").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("automatic_logging_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("automatic_logging_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("open_telemetry_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("open_telemetry_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
            }

            if (!Schema.Table("server_configurations").Column("migrations_run_on_startup").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("migrations_run_on_startup").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("server_configurations").Column("realtime_contract_mode").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("realtime_contract_mode").AsString(16).NotNullable().WithDefaultValue("auto");
            }
        }

        if (Schema.Table("client_configurations").Exists())
        {
            if (!Schema.Table("client_configurations").Column("department_auth_shadow_mode").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("department_auth_shadow_mode").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("push_enabled").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("push_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("winget_catalog_enabled").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("winget_catalog_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("remote_access_enabled").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("remote_access_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("remote_access_recording_enabled").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("remote_access_recording_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("remote_access_recording_auto_delete_expired").AsBoolean().Nullable();
            }

            if (!Schema.Table("client_configurations").Column("automatic_logging_enabled").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("automatic_logging_enabled").AsBoolean().Nullable();
            }
        }

        if (Schema.Table("site_configurations").Exists())
        {
            if (!Schema.Table("site_configurations").Column("department_auth_shadow_mode").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("department_auth_shadow_mode").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("push_enabled").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("push_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("winget_catalog_enabled").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("winget_catalog_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("remote_access_enabled").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("remote_access_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("remote_access_recording_enabled").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("remote_access_recording_enabled").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("remote_access_recording_auto_delete_expired").AsBoolean().Nullable();
            }

            if (!Schema.Table("site_configurations").Column("automatic_logging_enabled").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("automatic_logging_enabled").AsBoolean().Nullable();
            }
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Exists())
        {
            if (Schema.Table("site_configurations").Column("department_auth_shadow_mode").Exists())
                Delete.Column("department_auth_shadow_mode").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("push_enabled").Exists())
                Delete.Column("push_enabled").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("winget_catalog_enabled").Exists())
                Delete.Column("winget_catalog_enabled").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("remote_access_enabled").Exists())
                Delete.Column("remote_access_enabled").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("remote_access_recording_enabled").Exists())
                Delete.Column("remote_access_recording_enabled").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
                Delete.Column("remote_access_recording_auto_delete_expired").FromTable("site_configurations");
            if (Schema.Table("site_configurations").Column("automatic_logging_enabled").Exists())
                Delete.Column("automatic_logging_enabled").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Exists())
        {
            if (Schema.Table("client_configurations").Column("department_auth_shadow_mode").Exists())
                Delete.Column("department_auth_shadow_mode").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("push_enabled").Exists())
                Delete.Column("push_enabled").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("winget_catalog_enabled").Exists())
                Delete.Column("winget_catalog_enabled").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("remote_access_enabled").Exists())
                Delete.Column("remote_access_enabled").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("remote_access_recording_enabled").Exists())
                Delete.Column("remote_access_recording_enabled").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
                Delete.Column("remote_access_recording_auto_delete_expired").FromTable("client_configurations");
            if (Schema.Table("client_configurations").Column("automatic_logging_enabled").Exists())
                Delete.Column("automatic_logging_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Exists())
        {
            if (Schema.Table("server_configurations").Column("department_auth_shadow_mode").Exists())
                Delete.Column("department_auth_shadow_mode").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("push_enabled").Exists())
                Delete.Column("push_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("winget_catalog_enabled").Exists())
                Delete.Column("winget_catalog_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("remote_access_enabled").Exists())
                Delete.Column("remote_access_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("remote_access_recording_enabled").Exists())
                Delete.Column("remote_access_recording_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("remote_access_recording_auto_delete_expired").Exists())
                Delete.Column("remote_access_recording_auto_delete_expired").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("automatic_logging_enabled").Exists())
                Delete.Column("automatic_logging_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("open_telemetry_enabled").Exists())
                Delete.Column("open_telemetry_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("migrations_run_on_startup").Exists())
                Delete.Column("migrations_run_on_startup").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("realtime_contract_mode").Exists())
                Delete.Column("realtime_contract_mode").FromTable("server_configurations");
        }
    }
}
