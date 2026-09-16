using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// B8: corrige corrida de SequenceNumber em ai_chat_messages.
/// Dois requests concorrentes na mesma sessão calculavam nextSeq = max+1
/// sem transação, gerando sequências duplicadas e histórico desordenado
/// (BuildLlmMessagesFromHistory faz OrderBy(SequenceNumber)).
/// Remove duplicatas existentes (mantém a primeira por ordem de inserção)
/// e cria índice único (session_id, sequence_number) que transforma a
/// corrida em erro visível (o chamador já loga) em vez de corrupção silenciosa.
/// </summary>
[Migration(20260901_152)]
public class M152_AiChatMessageSequenceUnique : Migration
{
    public override void Up()
    {
        // Dedup: mantém o registro mais antigo (menor id) de cada (session_id, sequence_number).
        Execute.WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM ai_chat_messages a
                USING ai_chat_messages b
                WHERE a.session_id = b.session_id
                  AND a.sequence_number = b.sequence_number
                  AND a.id > b.id
                """;
            command.ExecuteNonQuery();
        });

        // Índice único composto via SQL nativo (a API fluente não encadeia
        // Unique() após Ascending() em índices compostos).
        Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_ai_chat_messages_session_seq ON ai_chat_messages (session_id, sequence_number)");
    }

    public override void Down()
    {
        Delete.Index("ux_ai_chat_messages_session_seq").OnTable("ai_chat_messages");
    }
}
