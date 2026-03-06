using FluentMigrator;

namespace Meduza.Migrations.Migrations;

/// <summary>
/// Define padrão false para todas as configurações booleanas e normaliza nulos existentes
/// em client/site para false.
/// </summary>
[Migration(20260304_025)]
public class M025_SetBooleanConfigurationDefaultsFalse : Migration
{
    public override void Up()
    {
        // Server: garantir defaults false nas flags booleanas globais.
        Alter.Table("server_configurations").AlterColumn("recovery_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("server_configurations").AlterColumn("discovery_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("server_configurations").AlterColumn("p2p_files_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("server_configurations").AlterColumn("support_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("server_configurations").AlterColumn("knowledge_base_enabled").AsBoolean().NotNullable().WithDefaultValue(false);

        // Client/Site: manter nullable por compatibilidade, mas com default false em novos inserts.
        Alter.Table("client_configurations").AlterColumn("recovery_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("client_configurations").AlterColumn("discovery_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("client_configurations").AlterColumn("p2p_files_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("client_configurations").AlterColumn("support_enabled").AsBoolean().Nullable().WithDefaultValue(false);

        Alter.Table("site_configurations").AlterColumn("recovery_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("site_configurations").AlterColumn("discovery_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("site_configurations").AlterColumn("p2p_files_enabled").AsBoolean().Nullable().WithDefaultValue(false);
        Alter.Table("site_configurations").AlterColumn("support_enabled").AsBoolean().Nullable().WithDefaultValue(false);

        // Normalização de legado: qualquer null booleano vira false.
        Execute.WithConnection((connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                UPDATE client_configurations
                   SET recovery_enabled = COALESCE(recovery_enabled, FALSE),
                       discovery_enabled = COALESCE(discovery_enabled, FALSE),
                       p2p_files_enabled = COALESCE(p2p_files_enabled, FALSE),
                       support_enabled = COALESCE(support_enabled, FALSE)
                 WHERE recovery_enabled IS NULL
                    OR discovery_enabled IS NULL
                    OR p2p_files_enabled IS NULL
                    OR support_enabled IS NULL;

                UPDATE site_configurations
                   SET recovery_enabled = COALESCE(recovery_enabled, FALSE),
                       discovery_enabled = COALESCE(discovery_enabled, FALSE),
                       p2p_files_enabled = COALESCE(p2p_files_enabled, FALSE),
                       support_enabled = COALESCE(support_enabled, FALSE)
                 WHERE recovery_enabled IS NULL
                    OR discovery_enabled IS NULL
                    OR p2p_files_enabled IS NULL
                    OR support_enabled IS NULL;";

            cmd.ExecuteNonQuery();
        });
    }

    public override void Down()
    {
        // Reverte apenas defaults para estado anterior conhecido (server histórico true/false misto).
        Alter.Table("server_configurations").AlterColumn("recovery_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        Alter.Table("server_configurations").AlterColumn("discovery_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        Alter.Table("server_configurations").AlterColumn("p2p_files_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        Alter.Table("server_configurations").AlterColumn("support_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        Alter.Table("server_configurations").AlterColumn("knowledge_base_enabled").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("client_configurations").AlterColumn("recovery_enabled").AsBoolean().Nullable();
        Alter.Table("client_configurations").AlterColumn("discovery_enabled").AsBoolean().Nullable();
        Alter.Table("client_configurations").AlterColumn("p2p_files_enabled").AsBoolean().Nullable();
        Alter.Table("client_configurations").AlterColumn("support_enabled").AsBoolean().Nullable();

        Alter.Table("site_configurations").AlterColumn("recovery_enabled").AsBoolean().Nullable();
        Alter.Table("site_configurations").AlterColumn("discovery_enabled").AsBoolean().Nullable();
        Alter.Table("site_configurations").AlterColumn("p2p_files_enabled").AsBoolean().Nullable();
        Alter.Table("site_configurations").AlterColumn("support_enabled").AsBoolean().Nullable();
    }
}
