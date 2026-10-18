namespace Discovery.Core.Interfaces;

/// <summary>
/// Fecha o ciclo de uma instalação de patch: interpreta o resultado do comando
/// (JSON por update) e atualiza o inventário + snapshot do agente.
/// </summary>
public interface IPatchExecutionResultService
{
    Task HandleCommandResultAsync(
        Guid commandId,
        int exitCode,
        string? output,
        string? errorMessage,
        DateTime completedAt,
        CancellationToken ct = default);
}
