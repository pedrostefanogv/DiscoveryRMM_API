using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove os metadados declarativos e os JSONs de contrato do script de automação.
///
/// - execution_frequency / trigger_modes_json eram rótulos de catálogo que não
///   agendavam nada (quem agenda é a tarefa: gatilhos + ScheduleCron);
/// - parameters_schema_json não era lido por nenhum runtime;
/// - metadata_json era lido pelo agent apenas para as opções do PSADT Welcome,
///   que agora devem ser enviadas no CommandPayload da tarefa.
/// </summary>
[Migration(20261211_221)]
public class M221_DropAutomationScriptMetadata : Migration
{
    private const string Table = "automation_script_definitions";

    public override void Up()
    {
        Delete.Column("execution_frequency").FromTable(Table);
        Delete.Column("trigger_modes_json").FromTable(Table);
        Delete.Column("parameters_schema_json").FromTable(Table);
        Delete.Column("metadata_json").FromTable(Table);
    }

    public override void Down()
    {
        Alter.Table(Table)
            .AddColumn("execution_frequency").AsString(100).NotNullable().WithDefaultValue("manual")
            .AddColumn("trigger_modes_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
            .AddColumn("parameters_schema_json").AsCustom("jsonb").Nullable()
            .AddColumn("metadata_json").AsCustom("jsonb").Nullable();
    }
}
