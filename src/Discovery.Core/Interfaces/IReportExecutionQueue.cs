namespace Discovery.Core.Interfaces;

/// <summary>
/// Fila em memoria para disparo imediato de execucoes de relatorios criadas
/// pelo endpoint POST /reports/run. O Quartz ReportGenerationJob continua como
/// fallback duravel caso a fila esteja cheia ou o processo reinicie.
/// </summary>
public interface IReportExecutionQueue
{
    bool TryEnqueue(Guid executionId, Guid? clientId);
}
