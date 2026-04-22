using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260307_041)]
public class M041_AddExecutionSchemaJsonToReportTemplates : Migration
{
    public override void Up()
    {
        Alter.Table("report_templates")
            .AddColumn("execution_schema_json").AsCustom("jsonb").Nullable();

        // Comentário: execution_schema_json permite que cada template tenha configuração
        // customizada de filtros obrigatórios, ordenação permitida, orientação etc.
        // Se NULL, usa o schema padrão do dataset (derivado em runtime).
    }

    public override void Down()
    {
        Delete.Column("execution_schema_json").FromTable("report_templates");
    }
}
