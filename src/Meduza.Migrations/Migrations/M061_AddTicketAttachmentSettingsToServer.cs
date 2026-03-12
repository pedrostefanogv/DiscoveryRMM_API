using FluentMigrator;

namespace Meduza.Migrations.Migrations;

/// <summary>
/// Adiciona configuração global de anexos de tickets em server_configurations.
/// Permite controlar habilitação, tipos MIME permitidos e tamanho máximo de arquivo.
/// </summary>
[Migration(20260312_061)]
public class M061_AddTicketAttachmentSettingsToServer : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("ticket_attachment_settings_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}");
    }

    public override void Down()
    {
        Delete.Column("ticket_attachment_settings_json").FromTable("server_configurations");
    }
}
