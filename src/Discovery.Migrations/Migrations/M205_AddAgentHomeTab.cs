using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Página inicial do agent herdável (servidor → cliente → site).
///
/// Define qual aba o painel do agent abre ao iniciar (status, loja, chat IA,
/// suporte, base de conhecimento, ...). O servidor carrega o default global
/// ("status"); cliente e site podem sobrescrever e ficam nulos (herdam) até que
/// alguém escolha um valor. Se a aba configurada estiver desabilitada para o
/// agent, ele cai para a aba de status.
/// </summary>
[Migration(20261124_205)]
public class M205_AddAgentHomeTab : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists()
            && !Schema.Table("server_configurations").Column("agent_home_tab").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("agent_home_tab").AsString(64).NotNullable().WithDefaultValue("status");
        }

        if (Schema.Table("client_configurations").Exists()
            && !Schema.Table("client_configurations").Column("agent_home_tab").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("agent_home_tab").AsString(64).Nullable();
        }

        if (Schema.Table("site_configurations").Exists()
            && !Schema.Table("site_configurations").Column("agent_home_tab").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("agent_home_tab").AsString(64).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Exists()
            && Schema.Table("server_configurations").Column("agent_home_tab").Exists())
        {
            Delete.Column("agent_home_tab").FromTable("server_configurations");
        }

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("agent_home_tab").Exists())
        {
            Delete.Column("agent_home_tab").FromTable("client_configurations");
        }

        if (Schema.Table("site_configurations").Exists()
            && Schema.Table("site_configurations").Column("agent_home_tab").Exists())
        {
            Delete.Column("agent_home_tab").FromTable("site_configurations");
        }
    }
}
