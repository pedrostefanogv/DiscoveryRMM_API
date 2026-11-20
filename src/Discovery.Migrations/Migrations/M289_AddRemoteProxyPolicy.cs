using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Allowlist de proxy/túnel do acesso remoto, HERDÁVEL (servidor → cliente → site),
/// mais a flag que mantém as entradas GLOBAIS inertes até liberação explícita.
///
/// Antes disto a allowlist só existia em `RemoteAccess:Proxy:DefaultAllowlist`
/// (appsettings.json) e era lida direto de IOptions: nenhuma tela conseguia
/// alterá-la e o default VAZIO bloqueava todo destino, com o erro
/// "allowlist vazia: acesso bloqueado para <ip>:<porta>" vindo do agent.
///
/// DEFAULT IDÊNTICO AO DE HOJE (zero surpresa no rollout):
///   server_configurations.remote_proxy_policy_json          -> '{}'
///   server_configurations.remote_proxy_global_entries_enabled -> false
///
/// `{}` = allowlist vazia = bloqueio total, exatamente o comportamento atual do
/// appsettings. Não há backfill retroativo: o default do servidor já preserva a
/// frota, e ninguém ganha acesso novo por conta desta migration.
///
/// Semântica por nível:
///   server_configurations.*       -> NOT NULL DEFAULT (valor de hoje);
///   client/site_configurations.*  -> NULLABLE (null = herda). A flag de entradas
///     globais NÃO tem coluna em cliente/site de propósito: ela governa apenas o
///     escopo servidor, e permitir override por cliente deixaria um cliente
///     reativar entradas globais que o operador desligou.
/// </summary>
// Version (YYYYMMDD_SSS) tem de ser MAIOR que a da migração anterior (M288 =
// 20270121_288): versões fora de ordem aplicam a migração errada ou não aplicam,
// deixando o EF mapeando coluna inexistente. Há um teste que guarda isso.
[Migration(20270122_289)]
public class M289_AddRemoteProxyPolicy : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists())
        {
            if (!Schema.Table("server_configurations").Column("remote_proxy_policy_json").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("remote_proxy_policy_json").AsCustom("text").NotNullable().WithDefaultValue("{}");
            }

            if (!Schema.Table("server_configurations").Column("remote_proxy_global_entries_enabled").Exists())
            {
                Alter.Table("server_configurations")
                    .AddColumn("remote_proxy_global_entries_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
            }
        }

        if (Schema.Table("client_configurations").Exists())
        {
            if (!Schema.Table("client_configurations").Column("remote_proxy_policy_json").Exists())
            {
                Alter.Table("client_configurations")
                    .AddColumn("remote_proxy_policy_json").AsCustom("text").Nullable();
            }
        }

        if (Schema.Table("site_configurations").Exists())
        {
            if (!Schema.Table("site_configurations").Column("remote_proxy_policy_json").Exists())
            {
                Alter.Table("site_configurations")
                    .AddColumn("remote_proxy_policy_json").AsCustom("text").Nullable();
            }
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Exists()
            && Schema.Table("site_configurations").Column("remote_proxy_policy_json").Exists())
        {
            Delete.Column("remote_proxy_policy_json").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("remote_proxy_policy_json").Exists())
        {
            Delete.Column("remote_proxy_policy_json").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Exists())
        {
            if (Schema.Table("server_configurations").Column("remote_proxy_global_entries_enabled").Exists())
                Delete.Column("remote_proxy_global_entries_enabled").FromTable("server_configurations");
            if (Schema.Table("server_configurations").Column("remote_proxy_policy_json").Exists())
                Delete.Column("remote_proxy_policy_json").FromTable("server_configurations");
        }
    }
}
