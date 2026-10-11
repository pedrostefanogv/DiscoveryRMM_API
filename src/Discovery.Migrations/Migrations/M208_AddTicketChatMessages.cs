using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Mensagens do chat de suporte.
///
/// sequence_number é alocado pelo servidor dentro de uma transação (max+1 sob
/// lock) e protegido por índice único — mesmo tratamento dado ao chat de IA em
/// M152 (corrida vira erro visível, não corrupção silenciosa).
///
/// client_message_id é a chave de idempotência ponta a ponta: o emissor (agent
/// ou console) gera um GUID e o servidor deduplica. Com o JetStream
/// DuplicateWindow do stream DISCOVERY_SUPPORT_CHAT cobrindo a reentrega no
/// broker, o índice único garante "exactly-once" na persistência mesmo com
/// entrega at-least-once.
/// </summary>
[Migration(20261126_208)]
public class M208_AddTicketChatMessages : Migration
{
    public override void Up()
    {
        Create.Table("ticket_chat_messages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("session_id").AsGuid().NotNullable().ForeignKey("ticket_chat_sessions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("sequence_number").AsInt64().NotNullable()
            .WithColumn("client_message_id").AsGuid().NotNullable()
            .WithColumn("author_kind").AsString(16).NotNullable()
            .WithColumn("author_user_id").AsGuid().Nullable()
            .WithColumn("author_name").AsString(255).Nullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_chat_messages_session_created")
            .OnTable("ticket_chat_messages")
            .OnColumn("session_id").Ascending()
            .OnColumn("created_at").Ascending();

        // Índices únicos compostos via SQL nativo (a API fluente não encadeia
        // Unique() após Ascending() em índices compostos — ver M152).
        Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_ticket_chat_messages_session_seq ON ticket_chat_messages (session_id, sequence_number)");
        Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_ticket_chat_messages_session_client ON ticket_chat_messages (session_id, client_message_id)");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_ticket_chat_messages_session_client");
        Execute.Sql("DROP INDEX IF EXISTS ux_ticket_chat_messages_session_seq");
        Delete.Index("ix_ticket_chat_messages_session_created").OnTable("ticket_chat_messages");
        Delete.Table("ticket_chat_messages");
    }
}
