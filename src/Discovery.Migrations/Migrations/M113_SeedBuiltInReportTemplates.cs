using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// [Historico] Esta migration semeava 17 templates embutidos em report_templates.
/// O seed foi DESATIVADO: instalacao limpa nao deve trazer templates prontos.
/// A classe, o atributo e o numero da versao foram mantidos para preservar a
/// cadeia de versoes do FluentMigrator (uma versao orfa nas bases que ja a
/// aplicaram quebraria o runner). O Down() segue idempotente e inofensivo.
/// O helper InsertTemplate() e os 17 layouts Get*Layout() foram removidos por
/// serem codigo morto — nenhuma referencia restante (conferido por grep).
/// </summary>
[Migration(20260429_113)]
public class M113_SeedBuiltInReportTemplates : Migration
{
    public override void Up()
    {
        // No-op: nada e inserido em report_templates.
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM report_templates WHERE is_built_in = true AND created_by = 'system';");
    }
}
