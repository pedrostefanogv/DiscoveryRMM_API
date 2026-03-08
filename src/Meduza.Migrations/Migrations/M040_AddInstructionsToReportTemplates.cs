using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_040)]
public class M040_AddInstructionsToReportTemplates : Migration
{
    public override void Up()
    {
        Alter.Table("report_templates")
            .AddColumn("instructions").AsString(4000).Nullable();

        Execute.Sql(@"
UPDATE report_templates
SET instructions = CASE dataset_type
    WHEN 0 THEN 'Use este relatorio para inventario de software. Filtros recomendados: clientId/siteId/agentId. Ordenacao sugerida: softwareName ASC ou lastSeenAt DESC.'
    WHEN 1 THEN 'Defina o periodo antes de executar. Campos obrigatorios: from e to (intervalo de datas). Opcionalmente informe clientId/siteId/agentId para escopo. Ordenacao sugerida: createdAt DESC.'
    WHEN 2 THEN 'Relatorio global de auditoria de configuracao. Campos obrigatorios: from e to. Opcional: changedBy, entityType, fieldName. Ordenacao sugerida: changedAt DESC.'
    WHEN 3 THEN 'Relatorio de tickets. Recomenda-se informar clientId/siteId e periodo (from/to). Ordenacao comum: createdAt DESC ou priority ASC.'
    WHEN 4 THEN 'Inventario de hardware atual. Escopo opcional por clientId/siteId/agentId. Ordenacao comum: siteName ASC ou collectedAt DESC.'
    ELSE instructions
END
WHERE instructions IS NULL;
");
    }

    public override void Down()
    {
        Delete.Column("instructions").FromTable("report_templates");
    }
}
