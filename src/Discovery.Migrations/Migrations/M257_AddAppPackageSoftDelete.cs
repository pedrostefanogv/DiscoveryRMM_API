using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Lixeira (soft delete) dos pacotes custom da Loja de Softwares:
/// is_deleted/deleted_at/deleted_by/delete_reason marcam a exclusão;
/// purge_failed_at/purge_error retêm a linha quando o purge não conseguiu
/// remover o arquivo/ícone no object storage (o operador pode forçar).
///
/// O índice (installation_type, is_deleted) cobre as leituras de catálogo,
/// que passam a filtrar is_deleted = false por padrão.
/// </summary>
[Migration(20261231_257)]
public class M257_AddAppPackageSoftDelete : Migration
{
    private const string Table = "app_packages";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("is_deleted").Exists())
            Alter.Table(Table).AddColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false);

        if (!Schema.Table(Table).Column("deleted_at").Exists())
            Alter.Table(Table).AddColumn("deleted_at").AsCustom("timestamptz").Nullable();

        if (!Schema.Table(Table).Column("deleted_by").Exists())
            Alter.Table(Table).AddColumn("deleted_by").AsString(256).Nullable();

        if (!Schema.Table(Table).Column("delete_reason").Exists())
            Alter.Table(Table).AddColumn("delete_reason").AsString(2000).Nullable();

        if (!Schema.Table(Table).Column("purge_failed_at").Exists())
            Alter.Table(Table).AddColumn("purge_failed_at").AsCustom("timestamptz").Nullable();

        if (!Schema.Table(Table).Column("purge_error").Exists())
            Alter.Table(Table).AddColumn("purge_error").AsString(2000).Nullable();

        // FluentMigrator versiona a migração (roda uma vez); os índices do
        // projeto são criados sem guarda de existência — seguimos o mesmo padrão.
        Create.Index("ix_app_packages_installation_is_deleted")
            .OnTable(Table)
            .OnColumn("installation_type").Ascending()
            .OnColumn("is_deleted").Ascending();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        Delete.Index("ix_app_packages_installation_is_deleted").OnTable(Table);

        if (Schema.Table(Table).Column("purge_error").Exists())
            Delete.Column("purge_error").FromTable(Table);

        if (Schema.Table(Table).Column("purge_failed_at").Exists())
            Delete.Column("purge_failed_at").FromTable(Table);

        if (Schema.Table(Table).Column("delete_reason").Exists())
            Delete.Column("delete_reason").FromTable(Table);

        if (Schema.Table(Table).Column("deleted_by").Exists())
            Delete.Column("deleted_by").FromTable(Table);

        if (Schema.Table(Table).Column("deleted_at").Exists())
            Delete.Column("deleted_at").FromTable(Table);

        if (Schema.Table(Table).Column("is_deleted").Exists())
            Delete.Column("is_deleted").FromTable(Table);
    }
}
