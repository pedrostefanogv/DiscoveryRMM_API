using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Configuração global de sensores em JSON (kill switch, intervalo default,
/// auto-ticket e notificações). Blob JSON evita mexer em catálogo/resolvedor
/// de configuração — mesmo padrão de retention_settings_json.
/// </summary>
[Migration(20270101_264)]
public class M264_AddSensorsSettings : Migration
{
    private const string Table = "server_configurations";

    public override void Up()
    {
        if (!Schema.Table(Table).Column("sensors_settings_json").Exists())
        {
            // NOT NULL DEFAULT '{}' para casar com o modelo (string não-anulável
            // com default) — o SchemaDriftHealthCheck compara modelo x banco.
            Alter.Table(Table)
                .AddColumn("sensors_settings_json")
                .AsCustom("jsonb")
                .NotNullable()
                .WithDefaultValue("{}");
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column("sensors_settings_json").Exists())
        {
            Delete.Column("sensors_settings_json").FromTable(Table);
        }
    }
}
