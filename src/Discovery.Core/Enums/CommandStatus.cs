namespace Discovery.Core.Enums;

public enum CommandStatus
{
    Pending = 0,
    Sent = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Timeout = 6,

    /// <summary>
    /// O agente aceitou o comando mas não o executou agora (adiado para a janela
    /// de manutenção ou adiado pelo usuário). É terminal: nada foi executado na
    /// máquina e nenhum resultado posterior corrige isso. Fica FORA das listas
    /// explícitas de reentrega/expiração (Pending/Sent/Running).
    /// </summary>
    Deferred = 7
}
