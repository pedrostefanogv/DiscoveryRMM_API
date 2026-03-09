using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_040)]
public class M040_AddInstructionsToReportTemplates : Migration
{
    public override void Up()
    {
        Alter.Table("report_templates")
            .AddColumn("instructions").AsString(4000).Nullable();

        Update.Table("report_templates")
            .Set(new
            {
                instructions = "Use este relatorio para inventario de software. Filtros recomendados: clientId/siteId/agentId. Ordenacao sugerida: softwareName ASC ou lastSeenAt DESC."
            })
            .Where(new { dataset_type = 0, instructions = (string?)null });

        Update.Table("report_templates")
            .Set(new
            {
                instructions = "Defina o periodo antes de executar. Campos obrigatorios: from e to (intervalo de datas). Opcionalmente informe clientId/siteId/agentId para escopo. Ordenacao sugerida: createdAt DESC."
            })
            .Where(new { dataset_type = 1, instructions = (string?)null });

        Update.Table("report_templates")
            .Set(new
            {
                instructions = "Relatorio global de auditoria de configuracao. Campos obrigatorios: from e to. Opcional: changedBy, entityType, fieldName. Ordenacao sugerida: changedAt DESC."
            })
            .Where(new { dataset_type = 2, instructions = (string?)null });

        Update.Table("report_templates")
            .Set(new
            {
                instructions = "Relatorio de tickets. Recomenda-se informar clientId/siteId e periodo (from/to). Ordenacao comum: createdAt DESC ou priority ASC."
            })
            .Where(new { dataset_type = 3, instructions = (string?)null });

        Update.Table("report_templates")
            .Set(new
            {
                instructions = "Inventario de hardware atual. Escopo opcional por clientId/siteId/agentId. Ordenacao comum: siteName ASC ou collectedAt DESC."
            })
            .Where(new { dataset_type = 4, instructions = (string?)null });
    }

    public override void Down()
    {
        Delete.Column("instructions").FromTable("report_templates");
    }
}
