using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Soft delete de comentários de chamado: o comentário continua legível (o
/// conteúdo permanece e é devolvido na query) mas fica marcado como excluído,
/// com autor da exclusão. Os anexos do comentário são soft-deletados junto.
/// </summary>
[Migration(20260312_212)]
public class M212_AddSoftDeleteToTicketComments : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_comments").Column("deleted_at").Exists())
            Alter.Table("ticket_comments").AddColumn("deleted_at").AsCustom("timestamptz").Nullable();

        if (!Schema.Table("ticket_comments").Column("deleted_by").Exists())
            Alter.Table("ticket_comments").AddColumn("deleted_by").AsString(200).Nullable();

        if (!Schema.Table("ticket_comments").Index("ix_ticket_comments_deleted_at").Exists())
            Create.Index("ix_ticket_comments_deleted_at")
                .OnTable("ticket_comments")
                .OnColumn("deleted_at").Ascending();
    }

    public override void Down()
    {
        if (Schema.Table("ticket_comments").Index("ix_ticket_comments_deleted_at").Exists())
            Delete.Index("ix_ticket_comments_deleted_at").OnTable("ticket_comments");

        if (Schema.Table("ticket_comments").Column("deleted_by").Exists())
            Delete.Column("deleted_by").FromTable("ticket_comments");

        if (Schema.Table("ticket_comments").Column("deleted_at").Exists())
            Delete.Column("deleted_at").FromTable("ticket_comments");
    }
}
