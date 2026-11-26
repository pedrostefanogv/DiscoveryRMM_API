using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cofre TPM de credenciais de conexão do agent (tpm_recovery_enabled).
///
/// Configuração herdável servidor → cliente → site, no mesmo molde do
/// p2p_files_enabled / updates_tab_enabled: o cofre guarda as credenciais de
/// conexão do agent no TPM da máquina. É OPT-IN, então o default global nasce
/// DESLIGADO (zero mudança de comportamento no rollout).
///
/// Semântica por nível:
///   server_configurations.tpm_recovery_enabled -> NOT NULL DEFAULT false;
///   client_configurations.tpm_recovery_enabled -> NULLABLE (null = herda servidor);
///   site_configurations.tpm_recovery_enabled   -> NULLABLE (null = herda cliente/servidor).
///
/// Precedência efetiva (igual as demais features): site ?? client ?? server.
///
/// ATENÇÃO: esta migration apenas DEFINE o schema (aplicada pelo MigrateUp no
/// boot da API). Nenhum ALTER é destrutivo.
/// </summary>
[Migration(20270129_300)]
public class M300_AddTpmRecoveryEnabled : Migration
{
    private const string ColumnName = "tpm_recovery_enabled";

    public override void Up()
    {
        // Default global DESLIGADO (opt-in). Atende a linha singleton existente
        // e as novas sem backfill.
        if (!Schema.Table("server_configurations").Column(ColumnName).Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn(ColumnName)
                .AsBoolean()
                .NotNullable()
                .WithDefaultValue(false);
        }

        // Cliente e site são NULLABLE: null significa "herda do nível acima".
        if (!Schema.Table("client_configurations").Column(ColumnName).Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn(ColumnName)
                .AsBoolean()
                .Nullable();
        }

        if (!Schema.Table("site_configurations").Column(ColumnName).Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn(ColumnName)
                .AsBoolean()
                .Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Column(ColumnName).Exists())
            Delete.Column(ColumnName).FromTable("site_configurations");

        if (Schema.Table("client_configurations").Column(ColumnName).Exists())
            Delete.Column(ColumnName).FromTable("client_configurations");

        if (Schema.Table("server_configurations").Column(ColumnName).Exists())
            Delete.Column(ColumnName).FromTable("server_configurations");
    }
}
