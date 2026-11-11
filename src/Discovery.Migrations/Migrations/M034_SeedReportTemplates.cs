using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// [Historico] Esta migration semeava 5 templates de relatorio pre-definidos.
/// O seed foi DESATIVADO: instalacao limpa nao deve trazer templates prontos.
/// A classe, o atributo e o numero da versao foram mantidos para preservar a
/// cadeia de versoes do FluentMigrator (bases que ja aplicaram a versao 34
/// seguem identicas; bases novas apenas registram a versao sem inserir nada).
/// </summary>
[Migration(20260307_034, "Seed predefined report templates")]
public class M034_SeedReportTemplates : Migration
{
    public override void Up()
    {
        // No-op: nada e inserido em report_templates.
    }

    public override void Down()
    {
        // Remove seeded templates by created_by field
        Delete.FromTable("report_templates").Row(new { created_by = "migration" });
    }
}
