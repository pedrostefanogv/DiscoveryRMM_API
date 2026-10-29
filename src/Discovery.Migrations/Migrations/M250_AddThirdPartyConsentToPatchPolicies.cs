using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Consentimento para patches de aplicativos de terceiros (WU-19/WU-20, D22).
///
/// Duplo consentimento (modelo Breeze):
///  - `sources`: JSON array na política com "os" e/ou "third_party". Ausente =
///    apenas "os" (nenhuma mudança de comportamento para quem já usa);
///  - `allow_third_party_apps`: segundo interruptor. A auto-aprovação de app
///    de terceiros exige os DOIS — evita ligar terceiros por acidente ao mexer
///    em severidade/classificação.
///
/// Compatibilidade: NULL-able / DEFAULT false, idempotente.
/// </summary>
[Migration(20261229_250)]
public class M250_AddThirdPartyConsentToPatchPolicies : Migration
{
    private const string Table = "patch_policies";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("sources").Exists())
            Execute.Sql($"ALTER TABLE {Table} ADD COLUMN sources jsonb NULL");
        if (!Schema.Table(Table).Column("allow_third_party_apps").Exists())
            Execute.Sql($"ALTER TABLE {Table} ADD COLUMN allow_third_party_apps boolean NOT NULL DEFAULT false");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column("allow_third_party_apps").Exists())
            Delete.Column("allow_third_party_apps").FromTable(Table);
        if (Schema.Table(Table).Column("sources").Exists())
            Delete.Column("sources").FromTable(Table);
    }
}
