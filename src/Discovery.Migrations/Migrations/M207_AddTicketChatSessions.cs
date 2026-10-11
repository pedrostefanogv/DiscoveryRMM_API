using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Sessões de chat de suporte vinculadas a um chamado.
///
/// Fluxo: o atendente (console web) inicia a sessão -> o agent da máquina do
/// chamado recebe um convite (CommandType.TicketChatInvite) e o usuário final
/// confirma -> a sessão vira Active e o agent abre uma janela dedicada.
///
/// Estados: pending (convite enviado), active (usuário aceitou), declined
/// (recusou/expirou/busy), expired (convite venceu sem resposta), ended
/// (encerrada pelo usuário, pelo suporte ou pelo sistema).
///
/// Histórico é somente leitura no console web; o agent NÃO persiste conversa.
/// </summary>
[Migration(20261126_207)]
public class M207_AddTicketChatSessions : Migration
{
    public override void Up()
    {
        Create.Table("ticket_chat_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable().ForeignKey("tickets", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("site_id").AsGuid().Nullable()
            .WithColumn("status").AsString(24).NotNullable()
            .WithColumn("started_by_user_id").AsGuid().Nullable()
            .WithColumn("started_by_name").AsString(255).Nullable()
            .WithColumn("started_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("invite_expires_at").AsCustom("timestamptz").Nullable()
            .WithColumn("accepted_at").AsCustom("timestamptz").Nullable()
            .WithColumn("declined_at").AsCustom("timestamptz").Nullable()
            .WithColumn("declined_reason").AsString(64).Nullable()
            .WithColumn("ended_at").AsCustom("timestamptz").Nullable()
            .WithColumn("ended_by").AsString(16).Nullable()
            .WithColumn("end_reason").AsString(64).Nullable()
            .WithColumn("last_message_at").AsCustom("timestamptz").Nullable()
            .WithColumn("message_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_chat_sessions_ticket")
            .OnTable("ticket_chat_sessions")
            .OnColumn("ticket_id").Ascending()
            .OnColumn("started_at").Descending();

        Create.Index("ix_ticket_chat_sessions_agent_status")
            .OnTable("ticket_chat_sessions")
            .OnColumn("agent_id").Ascending()
            .OnColumn("status").Ascending();

        Create.Index("ix_ticket_chat_sessions_status_invite")
            .OnTable("ticket_chat_sessions")
            .OnColumn("status").Ascending()
            .OnColumn("invite_expires_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_ticket_chat_sessions_status_invite").OnTable("ticket_chat_sessions");
        Delete.Index("ix_ticket_chat_sessions_agent_status").OnTable("ticket_chat_sessions");
        Delete.Index("ix_ticket_chat_sessions_ticket").OnTable("ticket_chat_sessions");
        Delete.Table("ticket_chat_sessions");
    }
}
