namespace Discovery.Core.Enums;

public enum ReportFormat
{
    Xlsx = 0,

    // O valor 1 era Pdf. O PDF deixou de ser gerado no servidor (o Markdown/HTML
    // e salvo em PDF pela impressao do navegador), mas o numero NAO e reaproveitado
    // para manter a compatibilidade com os valores ja gravados no banco.
    Csv = 2,
    Markdown = 3
}
