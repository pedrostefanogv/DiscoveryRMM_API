using FluentMigrator;

namespace Meduza.Migrations.Migrations;

/// <summary>
/// Adiciona a flag CloudBootstrapEnabled ao sistema de configuração hierárquica.
/// - server_configurations: campo global (default false)
/// - client_configurations: campo nullable, null = herda do servidor
/// Permite ativar/desativar o bootstrap P2P via cloud por cliente via UI de configurações.
/// </summary>
[Migration(20260326_089)]
public class M089_AddCloudBootstrapEnabled : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("cloud_bootstrap_enabled").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("client_configurations")
            .AddColumn("cloud_bootstrap_enabled").AsBoolean().Nullable();
    }

    public override void Down()
    {
        Delete.Column("cloud_bootstrap_enabled").FromTable("client_configurations");
        Delete.Column("cloud_bootstrap_enabled").FromTable("server_configurations");
    }
}
