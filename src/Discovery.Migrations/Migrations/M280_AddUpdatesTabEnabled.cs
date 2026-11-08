using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Kill switch da aba "Atualizações" do painel do agent.
///
/// A aba "Atualizações" (apps de terceiros pendentes de update via winget/choco)
/// era a UNICA das abas do painel sem chave de desligamento: Loja, Chat IA,
/// Suporte e Base de Conhecimento ja eram gateadas por appStorePolicy/
/// chatAIEnabled/supportEnabled/knowledgeBaseEnabled, com heranca
/// servidor -> cliente -> site. Esta coluna fecha essa lacuna.
///
/// Semantica por nivel:
///   server_configurations.updates_tab_enabled -> NOT NULL DEFAULT true
///     (default global HABILITADO: zero mudanca de comportamento no rollout);
///   client_configurations.updates_tab_enabled -> NULLABLE (null = herda servidor);
///   site_configurations.updates_tab_enabled   -> NULLABLE (null = herda cliente/servidor).
///
/// Precedencia efetiva (igual as demais features): site ?? client ?? server.
///
/// Nao ha UPDATE/backfill retroativo de proposito: o DEFAULT true do servidor ja
/// preserva o comportamento atual para toda a frota existente (a aba continua
/// visivel ate alguem desligar explicitamente em algum nivel).
/// </summary>
[Migration(20270112_280)]
public class M280_AddUpdatesTabEnabled : Migration
{
    public override void Up()
    {
        // Default global = habilitado (kill switch desligado). Atende a linha
        // singleton existente e as novas.
        Alter.Table("server_configurations")
            .AddColumn("updates_tab_enabled")
            .AsBoolean()
            .NotNullable()
            .WithDefaultValue(true);

        // Cliente e site sao NULLABLE: null significa "herda do nivel acima",
        // mesma convencao das demais flags de funcionalidade.
        Alter.Table("client_configurations")
            .AddColumn("updates_tab_enabled")
            .AsBoolean()
            .Nullable();

        Alter.Table("site_configurations")
            .AddColumn("updates_tab_enabled")
            .AsBoolean()
            .Nullable();
    }

    public override void Down()
    {
        Delete.Column("updates_tab_enabled").FromTable("site_configurations");
        Delete.Column("updates_tab_enabled").FromTable("client_configurations");
        Delete.Column("updates_tab_enabled").FromTable("server_configurations");
    }
}
