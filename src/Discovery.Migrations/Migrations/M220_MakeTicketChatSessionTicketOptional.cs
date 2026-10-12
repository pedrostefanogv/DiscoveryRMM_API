using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Permite sessões de chat de suporte SEM chamado (avulsas), abertas direto da
/// lista/detalhe do agent.
///
/// O fluxo vinculado a chamado continua existindo (ticket_id preenchido); a
/// conversa avulsa nasce com ticket_id nulo e fica ligada apenas à máquina
/// (agent_id) e ao tenant (client_id/site_id). A FK para tickets permanece: o
/// cascade só age quando existe chamado.
/// </summary>
[Migration(20261210_220)]
public class M220_MakeTicketChatSessionTicketOptional : Migration
{
    public override void Up()
    {
        Alter.Table("ticket_chat_sessions")
            .AlterColumn("ticket_id").AsGuid().Nullable();
    }

    public override void Down()
    {
        // Não há como voltar a NOT NULL com sessões avulsas: elas são removidas
        // (o histórico canônico de conversas com chamado é preservado).
        Execute.Sql("DELETE FROM ticket_chat_sessions WHERE ticket_id IS NULL;");
        Alter.Table("ticket_chat_sessions")
            .AlterColumn("ticket_id").AsGuid().NotNullable();
    }
}
