using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Catálogo de aplicativos de terceiros (WU-21, decisão D24).
///
/// Curadoria sobre manifestos do `winget-pkgs` (ou MSI corporativo): fixa
/// versão + SHA-256 + URL do fornecedor + switches silenciosos + ProductCode.
/// App de terceiros só é auto-aprovado se estiver aqui com status `active`
/// (revogar uma versão tem efeito imediato na aprovação).
///
/// Compatibilidade: tabela nova; nada existente muda. Idempotente.
/// </summary>
[Migration(20261230_251)]
public class M251_CreateAppCatalogEntries : Migration
{
    private const string Table = "app_catalog_entries";

    public override void Up()
    {
        if (Schema.Table(Table).Exists())
            return;

        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("source").AsString(20).NotNullable()
            .WithColumn("package_id").AsString(200).NotNullable()
            .WithColumn("name").AsString(300).NotNullable()
            .WithColumn("publisher").AsString(200).Nullable()
            .WithColumn("version").AsString(100).NotNullable()
            .WithColumn("installer_url").AsString(2000).NotNullable()
            .WithColumn("sha256").AsString(64).NotNullable()
            .WithColumn("installer_type").AsString(20).Nullable()
            .WithColumn("silent_args").AsString(500).Nullable()
            .WithColumn("product_code").AsString(100).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("active")
            .WithColumn("release_date").AsDateTime().Nullable()
            .WithColumn("notes").AsString(1000).Nullable()
            .WithColumn("curated_by").AsString(200).Nullable()
            .WithColumn("curated_at").AsDateTime().NotNullable()
            .WithColumn("revoked_by").AsString(200).Nullable()
            .WithColumn("revoked_at").AsDateTime().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        Execute.Sql($"CREATE UNIQUE INDEX IF NOT EXISTS ux_app_catalog_entries_pkg_version ON {Table} (source, package_id, version)");
        Execute.Sql($"CREATE INDEX IF NOT EXISTS ix_app_catalog_entries_status ON {Table} (status)");
    }

    public override void Down()
    {
        if (Schema.Table(Table).Exists())
            Delete.Table(Table);
    }
}
