using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// B8-r2 (segundo bug do chat 500, distinto do 401): "Duplicate
/// function_call_output for call_id '...'. Each function_call must have
/// exactly one matching function_call_output".
///
/// O agent (Go) reenviava na lista ToolResults uma chamada que JÁ havia sido
/// respondida no turno anterior. O bloco de toolResults do orquestrador só
/// validava PERTENCIMENTO do CallId às tool calls pendentes (HashSet) — nunca
/// se aquele call_id já tinha role='tool' no histórico, nem duplicatas dentro
/// do PRÓPRIO lote. As duas linhas eram persistidas e, no round seguinte,
/// BuildLlmMessagesFromHistory (OrderBy SequenceNumber) as convertia em DOIS
/// function_call_output para o mesmo call_id -> provedor 400, que o middleware
/// converte na resposta genérica "Erro interno do servidor" vista no cliente.
/// Caso real: sessão 01a1cd68-31a4-716d-ad2c-637be5849a65, seq 16 e 17,
/// call_856c0949dcd0b375_2 (get_pending_updates).
///
/// Defesa em profundidade: índice único parcial em ai_chat_messages
/// (session_id, tool_call_id) WHERE role='tool', transformando uma futura
/// regressão do chamador em erro VISÍVEL (o chamador já loga a falha de
/// persistência) em vez de histórico corrompido que só quebra no round
/// seguinte — longe da causa.
///
/// a2ui_action: a sentinela de clique em surface A2UI NÃO é tool call real e
/// pode legitimamente repetir. Ela é EXCLUÍDA do índice (e do dedupe abaixo),
/// preservando o tratamento especial do orquestrador.
/// </summary>
[Migration(20261111_160)]
public class M160_AiChatMessageToolCallUnique : Migration
{
    /// <summary>
    /// Predicado das linhas cobertas pelo índice: role='tool' com call_id
    /// informado e que NÃO sejam a sentinela a2ui_action.
    /// </summary>
    private const string DedupPredicate = """
        a.role = 'tool'
          AND a.tool_call_id IS NOT NULL
          AND btrim(a.tool_call_id) <> ''
          AND lower(a.tool_call_id) <> 'a2ui_action'
        """;

    public override void Up()
    {
        // Dedup dos dados JÁ existentes antes de criar o índice (sem isso o
        // CREATE UNIQUE INDEX falharia em qualquer base que já tenha o bug).
        // Mantém a PRIMEIRA resposta de cada call_id na sessão: a repetição é
        // justamente o retry tardio descartado pelo orchestrator.
        // Desempate por sequence_number (menor sequência) e depois por id
        // (menor) — determinístico, mesmo padrão do M152.
        Execute.WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM ai_chat_messages a
                USING ai_chat_messages b
                WHERE a.session_id = b.session_id
                  AND lower(a.tool_call_id) = lower(b.tool_call_id)
                  AND {DedupPredicate}
                  AND b.role = 'tool'
                  AND b.tool_call_id IS NOT NULL
                  AND btrim(b.tool_call_id) <> ''
                  AND lower(b.tool_call_id) <> 'a2ui_action'
                  AND (a.sequence_number, a.id) > (b.sequence_number, b.id)
                """;
            command.ExecuteNonQuery();
        });

        // Índice único PARCIAL via SQL nativo: só cobre role='tool' com
        // tool_call_id preenchido (mensagens user/assistant têm tool_call_id
        // NULL e não podem ser restringidas), e ignora a sentinela a2ui_action.
        Execute.Sql("""
            CREATE UNIQUE INDEX IF NOT EXISTS ux_ai_chat_messages_session_tool_call
              ON ai_chat_messages (session_id, lower(tool_call_id))
              WHERE role = 'tool'
                AND tool_call_id IS NOT NULL
                AND btrim(tool_call_id) <> ''
                AND lower(tool_call_id) <> 'a2ui_action'
            """);
    }

    public override void Down()
    {
        Delete.Index("ux_ai_chat_messages_session_tool_call").OnTable("ai_chat_messages");
    }
}
