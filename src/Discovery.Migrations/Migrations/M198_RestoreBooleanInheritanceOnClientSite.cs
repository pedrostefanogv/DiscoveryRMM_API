using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Restaura a herança real dos booleanos de cliente/site (NULL = herda).
///
/// A M025 normalizou "qualquer null booleano" para FALSE, o que tornou a herança
/// impossível: todo cliente/site passou a sobrescrever o servidor com false. Esta
/// migration converte os FALSE de volta para NULL e remove o DEFAULT FALSE das
/// colunas, para que novos registros também herdem.
///
/// ATENÇÃO: um FALSE gravado antes desta migration pode ter sido uma decisão
/// consciente de desligar a funcionalidade. Depois da conversão, o valor herdado do
/// servidor volta a valer até alguém definir false explicitamente de novo.
/// </summary>
[Migration(20261031_198)]
public class M198_RestoreBooleanInheritanceOnClientSite : Migration
{
    private static readonly string[] ClientBooleanColumns =
    [
        "recovery_enabled",
        "discovery_enabled",
        "p2p_files_enabled",
        "cloud_bootstrap_enabled",
        "support_enabled",
        "chat_ai_enabled",
        "knowledge_base_enabled"
    ];

    private static readonly string[] SiteBooleanColumns =
    [
        "recovery_enabled",
        "discovery_enabled",
        "p2p_files_enabled",
        "support_enabled",
        "chat_ai_enabled",
        "knowledge_base_enabled"
    ];

    public override void Up()
    {
        // FALSE explícito -> NULL (herda). zero_touch_enabled já nasce NULL (M197).
        NullifyFalse("client_configurations", ClientBooleanColumns);
        NullifyFalse("site_configurations", SiteBooleanColumns);
    }

    public override void Down()
    {
        // Volta ao estado da M025: default false + nenhum null.
        NullToFalse("client_configurations", ClientBooleanColumns);
        NullToFalse("site_configurations", SiteBooleanColumns);
        RestoreFalseDefault("client_configurations", ClientBooleanColumns);
        RestoreFalseDefault("site_configurations", SiteBooleanColumns);
    }

    private void NullifyFalse(string table, string[] columns)
    {
        if (!Schema.Table(table).Exists()) return;

        foreach (var column in columns)
        {
            if (!Schema.Table(table).Column(column).Exists()) continue;

            Execute.Sql($"UPDATE {table} SET {column} = NULL WHERE {column} = FALSE;");
            Execute.Sql($"ALTER TABLE {table} ALTER COLUMN {column} DROP DEFAULT;");
        }
    }

    private void NullToFalse(string table, string[] columns)
    {
        if (!Schema.Table(table).Exists()) return;

        foreach (var column in columns)
        {
            if (!Schema.Table(table).Column(column).Exists()) continue;

            Execute.Sql($"UPDATE {table} SET {column} = FALSE WHERE {column} IS NULL;");
        }
    }

    private void RestoreFalseDefault(string table, string[] columns)
    {
        if (!Schema.Table(table).Exists()) return;

        foreach (var column in columns)
        {
            if (!Schema.Table(table).Column(column).Exists()) continue;

            Execute.Sql($"ALTER TABLE {table} ALTER COLUMN {column} SET DEFAULT FALSE;");
        }
    }
}
