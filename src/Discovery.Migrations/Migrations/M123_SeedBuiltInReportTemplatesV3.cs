using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// [Historico] Esta migration semeava 12 templates embutidos (dataset types V3).
/// O seed foi DESATIVADO: instalacao limpa nao deve trazer templates prontos.
/// A classe, o atributo e o numero da versao foram mantidos para preservar a
/// cadeia de versoes do FluentMigrator (uma versao orfa nas bases que ja a
/// aplicaram quebraria o runner). O Down() segue idempotente e inofensivo:
/// apagar IDs deterministas que nao existem e no-op.
/// </summary>
[Migration(20260706_123)]
public class M123_SeedBuiltInReportTemplatesV3 : Migration
{
    public override void Up()
    {
        // No-op: nada e inserido em report_templates.
    }

    public override void Down()
    {
        for (var i = 1; i <= 12; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000002-0000-0000-0000-00000000000{i:D1}") });
        }
    }
}
