using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona <c>ticket_id</c> em <c>file_upload_links</c> para permitir gerar links
/// de upload externo diretamente dentro de um chamado (para receber arquivos de
/// máquinas sem agent instalado).
/// </summary>
[Migration(20270129_303)]
public class M303_AddTicketIdToFileUploadLinks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("file_upload_links").Column("ticket_id").Exists())
        {
            Alter.Table("file_upload_links")
                .AddColumn("ticket_id")
                .AsGuid()
                .Nullable()
                .ForeignKey("fk_file_upload_links_ticket", "tickets", "id")
                .OnDelete(System.Data.Rule.SetNull);

            Create.Index("ix_file_upload_links_ticket_id")
                .OnTable("file_upload_links")
                .OnColumn("ticket_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("file_upload_links").Column("ticket_id").Exists())
        {
            Delete.Index("ix_file_upload_links_ticket_id").OnTable("file_upload_links");
            Delete.ForeignKey("fk_file_upload_links_ticket").OnTable("file_upload_links");
            Delete.Column("ticket_id").FromTable("file_upload_links");
        }
    }
}
