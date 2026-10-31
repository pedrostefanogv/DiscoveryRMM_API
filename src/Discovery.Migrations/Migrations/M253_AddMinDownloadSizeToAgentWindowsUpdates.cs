using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// MinDownloadSize dos updates do agente (menor download possível, delta/express).
///
/// O WUA reporta MaxDownloadSize como a soma de TODO o pacote empacotado (UUP):
/// em uma Insider Preview o mesmo update trazia 13.426 arquivos (todas as
/// edições/idiomas) e MaxDownloadSize = 97,8 GB, enquanto o download real era
/// ~9,1 GB. Com MinDownloadSize a UI exibe "a partir de X" em vez de um número
/// enganoso, e tamanhos acima do limiar de plausibilidade são omitidos.
///
/// Compatibilidade: coluna NULL-able; agente antigo não envia e fica null.
/// Idempotente no padrão M238/M239/M244.
/// </summary>
[Migration(20261030_253)]
public class M253_AddMinDownloadSizeToAgentWindowsUpdates : Migration
{
    private const string Table = "agent_windows_updates";
    private const string Column = "min_download_size";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {Column} bigint NULL");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
