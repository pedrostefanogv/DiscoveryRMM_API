namespace Discovery.Core.DTOs;

/// <summary>Progresso do reprocessamento de labels (antes o endpoint so respondia "iniciado" e nao havia como acompanhar).</summary>
public class AgentLabelReprocessProgress
{
    public AgentLabelReprocessProgress(int processed, int total, bool isCompleted, string? message)
    {
        Processed = processed;
        Total = total;
        IsCompleted = isCompleted;
        Message = message;
    }

    public int Processed { get; }
    public int Total { get; }
    public bool IsCompleted { get; }
    public string? Message { get; }

    public int Percent => Total <= 0 ? (IsCompleted ? 100 : 0) : (int)Math.Round(Processed * 100d / Total);
}

/// <summary>Estado atual de um job de reprocessamento.</summary>
public class AgentLabelReprocessStatusResponse
{
    public string JobId { get; set; } = string.Empty;
    public string State { get; set; } = "Unknown";
    public int Processed { get; set; }
    public int Total { get; set; }
    public int Percent { get; set; }
    public bool IsCompleted { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>Resposta do disparo de reprocessamento, com o id para acompanhamento.</summary>
public class AgentLabelReprocessResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
