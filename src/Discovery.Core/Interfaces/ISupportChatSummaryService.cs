namespace Discovery.Core.Interfaces;

/// <summary>
/// Gera um resumo estruturado por IA ao encerrar uma sessão de chat vinculada a chamado,
/// registrando o resultado na timeline do chamado de forma assíncrona e não-bloqueante.
/// </summary>
public interface ISupportChatSummaryService
{
    /// <summary>
    /// Agenda a geração do resumo em background usando um escopo de DI próprio.
    /// Nunca bloqueia nem propaga exceções para o fluxo de encerramento do chat.
    /// </summary>
    void ScheduleSessionSummary(Guid sessionId);

    /// <summary>
    /// Executa a geração e persistência do resumo da sessão (usado pelo job em background e testes).
    /// </summary>
    Task GenerateAndRecordSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
