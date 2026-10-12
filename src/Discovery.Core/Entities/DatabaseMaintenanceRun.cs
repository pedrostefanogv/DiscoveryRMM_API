namespace Discovery.Core.Entities;

/// <summary>
/// Histórico persistido de uma execução do <c>DatabaseMaintenanceJob</c>.
/// O histórico em memória (JobExecutionHistoryStore) some a cada restart; esta
/// tabela guarda o essencial para a tela de manutenção (quando rodou, o que
/// rodou, quanto recuperou e o que falhou).
/// </summary>
public class DatabaseMaintenanceRun
{
    public Guid Id { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>false quando houve ao menos uma operação com erro.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Tabelas descobertas na execução.</summary>
    public int TableCount { get; set; }

    /// <summary>Operações concluídas sem erro.</summary>
    public int SuccessCount { get; set; }

    /// <summary>Operações que falharam (lock timeout, permissão, etc.).</summary>
    public int ErrorCount { get; set; }

    /// <summary>Tamanho do banco ao final da execução (bytes).</summary>
    public long DatabaseSizeBytes { get; set; }

    /// <summary>Tamanho do banco no início menos no fim (pode ser negativo).</summary>
    public long ReclaimedBytes { get; set; }

    /// <summary>Resumo das fases/operacões em JSON (DatabaseMaintenanceOperations).</summary>
    public string OperationsJson { get; set; } = "[]";

    public string? ErrorMessage { get; set; }
}
