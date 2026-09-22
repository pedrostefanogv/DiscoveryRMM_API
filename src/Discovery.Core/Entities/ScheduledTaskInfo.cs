namespace Discovery.Core.Entities;

/// <summary>
/// Tarefa agendada do Windows (Task Scheduler) coletada pelo agent.
/// Persistida como parte do JSON de componentes de hardware do agent.
/// State distingue o estado da tarefa (enabled/disabled); Status é o runtime
/// atual informado pelo Task Scheduler (Ready/Running/...).
/// </summary>
public class ScheduledTaskInfo
{
    public string TaskPath { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    /// <summary>enabled | disabled</summary>
    public string State { get; set; } = string.Empty;
    /// <summary>Ready | Running | Queued | ...</summary>
    public string Status { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ActionPath { get; set; } = string.Empty;
    public string ActionArgs { get; set; } = string.Empty;
    /// <summary>boot | logon | daily | weekly | once | idle | event | other</summary>
    public string TriggerType { get; set; } = string.Empty;
    /// <summary>Descrição amigável do gatilho (ex.: "Diário às 03:00").</summary>
    public string TriggerDesc { get; set; } = string.Empty;
    public string NextRunTime { get; set; } = string.Empty;
    public string LastRunTime { get; set; } = string.Empty;
    public long LastResult { get; set; }
}
