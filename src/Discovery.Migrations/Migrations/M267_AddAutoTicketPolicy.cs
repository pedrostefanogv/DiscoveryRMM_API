using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Política de auto-ticket hierárquica: Server -> Client -> Site.
///
/// Cada nível guarda um blob JSON com APENAS os campos que sobrescreve; campo
/// ausente (ou coluna nula) HERDA do nível superior. Semântica idêntica à do
/// ConfigurationResolver do produto ("valores null em Client/Site são
/// preenchidos pelo nível superior").
///
/// Desligar em qualquer nível é apenas Enabled=false no blob daquele nível.
///
/// Nota: sensors_settings_json continua existindo SÓ em server_configurations
/// (config global de sensores). Estas colunas são o veículo de override do
/// auto-ticket, evitando duas fontes de verdade concorrentes.
/// </summary>
[Migration(20270102_267)]
public class M267_AddAutoTicketPolicy : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("auto_ticket_policy_json").AsCustom("jsonb").Nullable();

        Alter.Table("client_configurations")
            .AddColumn("auto_ticket_policy_json").AsCustom("jsonb").Nullable();

        Alter.Table("site_configurations")
            .AddColumn("auto_ticket_policy_json").AsCustom("jsonb").Nullable();
    }

    public override void Down()
    {
        Delete.Column("auto_ticket_policy_json").FromTable("site_configurations");
        Delete.Column("auto_ticket_policy_json").FromTable("client_configurations");
        Delete.Column("auto_ticket_policy_json").FromTable("server_configurations");
    }
}
