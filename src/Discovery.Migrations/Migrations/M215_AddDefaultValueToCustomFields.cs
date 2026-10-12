using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Valor padrão por campo customizado: usado quando a entidade ainda não tem
/// valor gravado (no portal e também na leitura feita pelo agente/script).
///
/// Armazenado como JSON (mesmo formato de custom_field_values.value_json) para
/// respeitar o tipo do campo (texto, número, booleano, lista).
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261202_215)]
public class M215_AddDefaultValueToCustomFields : Migration
{
    private const string Table = "custom_field_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("default_value_json").Exists())
        {
            Alter.Table(Table).AddColumn("default_value_json").AsString(int.MaxValue).Nullable();
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("default_value_json").Exists())
            Delete.Column("default_value_json").FromTable(Table);
    }
}
