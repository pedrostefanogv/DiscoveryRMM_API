using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// O formato PDF (valor 1 do enum ReportFormat) foi removido do produto:
/// o relatorio passa a ser gerado em Markdown/XLSX/CSV e o PDF e obtido a
/// partir do Markdown/HTML pela impressao do navegador.
///
/// Sem esta conversao, registros antigos com format = 1 continuariam
/// apontando para um formato que nao possui renderer registrado, fazendo
/// toda execucao agendada/reprocessada falhar com
/// "Format 1 is not enabled".
///
/// A migracao e apenas de dados e idempotente.
/// </summary>
[Migration(20261027_194)]
public class M194_ConvertReportPdfFormatToMarkdown : Migration
{
    private const int LegacyPdfFormat = 1;
    private const int MarkdownFormat = 3;

    public override void Up()
    {
        ConvertToMarkdown("report_templates", "default_format");
        ConvertToMarkdown("report_executions", "format");
        ConvertToMarkdown("report_schedules", "format");
    }

    public override void Down()
    {
        // Irreversivel por design: apos a conversao nao e possivel distinguir
        // os registros que eram PDF dos que ja usavam Markdown.
    }

    private void ConvertToMarkdown(string table, string column)
    {
        if (!Schema.Table(table).Exists())
            return;

        if (!Schema.Table(table).Column(column).Exists())
            return;

        Execute.Sql($"UPDATE {table} SET {column} = {MarkdownFormat} WHERE {column} = {LegacyPdfFormat};");
    }
}
