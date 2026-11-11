using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// [Historico] Esta migration semeava 8 templates embutidos de relatorio.
/// O seed foi DESATIVADO: instalacao limpa nao deve trazer templates prontos.
/// A classe, o atributo e o numero da versao foram mantidos para preservar a
/// cadeia de versoes do FluentMigrator (uma versao orfa nas bases que ja a
/// aplicaram quebraria o runner). O Down() segue idempotente e inofensivo.
/// </summary>
[Migration(20260528_121)]
public class M121_SeedBuiltInReportTemplates : Migration
{
    public override void Up()
    {
        // No-op: nada e inserido em report_templates.
    }

    public override void Down()
    {
        for (var i = 1; i <= 8; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000001-0000-0000-0000-00000000000{i}") });
        }
    }
}
