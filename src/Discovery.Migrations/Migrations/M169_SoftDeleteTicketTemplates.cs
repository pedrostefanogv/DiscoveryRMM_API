using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Soft delete dos templates de chamado: <c>deleted_at</c>/<c>deleted_by</c>
/// permitem a lixeira (restaurar/excluir definitivamente) sem perder o
/// histórico dos chamados abertos pelo template. Exclusão física passa a
/// existir apenas no purge explícito.
/// </summary>
[Migration(20260926_169)]
public class M169_SoftDeleteTicketTemplates : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_templates").Exists()) return;

        if (!Schema.Table("ticket_templates").Column("deleted_at").Exists())
            Alter.Table("ticket_templates").AddColumn("deleted_at").AsCustom("timestamptz").Nullable();

        if (!Schema.Table("ticket_templates").Column("deleted_by").Exists())
            Alter.Table("ticket_templates").AddColumn("deleted_by").AsString(255).Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table("ticket_templates").Exists()) return;

        if (Schema.Table("ticket_templates").Column("deleted_by").Exists())
            Delete.Column("deleted_by").FromTable("ticket_templates");

        if (Schema.Table("ticket_templates").Column("deleted_at").Exists())
            Delete.Column("deleted_at").FromTable("ticket_templates");
    }
}
