using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove as configurações de atualização automática de software
/// (auto_update_settings_json) de servidor, cliente e site.
///
/// O produto não faz mais auto-update de software: as atualizações passam a ser
/// manuais (aba "Atualizações" do agent / script de update). O campo não tinha
/// consumidor de produção no servidor — o self-update do agent
/// (agent_update_policy_json) é outra coisa e permanece.
/// </summary>
[Migration(20261101_199)]
public class M199_DropAutoUpdateSettings : Migration
{
    private const string Column = "auto_update_settings_json";

    public override void Up()
    {
        DropIfExists("server_configurations");
        DropIfExists("client_configurations");
        DropIfExists("site_configurations");
    }

    public override void Down()
    {
        // Recria as colunas com o tipo/default originais da M019. Os valores
        // configurados antes do drop não são recuperáveis.
        AddIfMissing("server_configurations", notNullable: true);
        AddIfMissing("client_configurations", notNullable: false);
        AddIfMissing("site_configurations", notNullable: false);
    }

    private void DropIfExists(string table)
    {
        if (Schema.Table(table).Exists() && Schema.Table(table).Column(Column).Exists())
        {
            Delete.Column(Column).FromTable(table);
        }
    }

    private void AddIfMissing(string table, bool notNullable)
    {
        if (!Schema.Table(table).Exists() || Schema.Table(table).Column(Column).Exists())
            return;

        if (notNullable)
        {
            Alter.Table(table).AddColumn(Column).AsCustom("text").NotNullable().WithDefaultValue("");
        }
        else
        {
            Alter.Table(table).AddColumn(Column).AsCustom("text").Nullable();
        }
    }
}
