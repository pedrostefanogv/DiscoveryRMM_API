using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Vínculo entre mensagens do chat de suporte e anexos.
///
/// Os bytes continuam no object storage via fluxo presigned existente
/// (IAttachmentService, EntityType "TicketChat"); esta tabela só amarra o
/// anexo à mensagem, preservando a ordem/autoridade de cada arquivo no
/// histórico do chamado.
/// </summary>
[Migration(20261126_209)]
public class M209_AddTicketChatMessageAttachments : Migration
{
    public override void Up()
    {
        Create.Table("ticket_chat_message_attachments")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable().ForeignKey("ticket_chat_messages", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("attachment_id").AsGuid().NotNullable().ForeignKey("attachments", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_chat_message_attachments_attachment")
            .OnTable("ticket_chat_message_attachments")
            .OnColumn("attachment_id").Ascending();

        Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_ticket_chat_message_attachments_msg_att ON ticket_chat_message_attachments (message_id, attachment_id)");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_ticket_chat_message_attachments_msg_att");
        Delete.Index("ix_ticket_chat_message_attachments_attachment").OnTable("ticket_chat_message_attachments");
        Delete.Table("ticket_chat_message_attachments");
    }
}
