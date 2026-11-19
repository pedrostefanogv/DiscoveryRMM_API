# ADR: Background Jobs — Quartz.NET

> **Status:** Implementado ✅  
> **Data:** 2026-11-19 (atualizado para Quartz.NET 4.7.3)  
> **Decisores:** Time DiscoveryRMM  

---

## Decisão

Migrar todos os jobs agendados e recorrentes de `IHostedService` / `BackgroundService` para **Quartz.NET 4.7.3** com store in-memory, `[DisallowConcurrentExecution]` em todos os jobs, e dashboard administrativo via `JobsController`.

Três serviços foram mantidos como `IHostedService` por design: `AgentPackagePrebuildHostedService` (one-time startup), `SyncPingDispatchBackgroundService` (padrão singleton com estado interno) e `AlertDispatchService` (scoped, não é background).

---

## Jobs Quartz implementados

| Job | Grupo | Schedule | Descrição |
|-----|-------|----------|-----------|
| `LogPurgeJob` | `maintenance` | `0 0 3 * * ?` (3AM) | Purge de logs antigos |
| `ReportRetentionJob` | `maintenance` | `0 0 4 * * ?` (4AM) | Purge de relatórios expirados |
| `AiChatRetentionJob` | `maintenance` | `0 0 2 * * ?` (2AM) | Soft/hard delete de chat sessions |
| `TicketChatMaintenanceJob` | `support-chat` | a cada 1 min | Expira convites e encerra sessões inativas/fechadas |
| `P2pMaintenanceJob` | `maintenance` | a cada 15 min | Limpeza de P2P presence + seed plans |
| `DataRetentionJob` | `maintenance` | `0 30 3 * * ?` (3:30AM) | Purge de sessões, tokens, notificações, comandos e telemetria |
| `DatabaseMaintenanceJob` | `maintenance` | `0 0 3 ? * SUN` (config) | VACUUM, REINDEX, ANALYZE, FREEZE conforme `DatabaseMaintenanceSettings` |
| `KnowledgeEmbeddingJob` | `kb` | a cada 30s (config) | Re-chunking + embeddings em batch |
| `TicketAnswerEmbeddingJob` | `kb` | a cada 60s (config) | Embeddings de respostas de questionários de tickets |
| `WingetManifestsSyncJob` | `catalog` | a cada 360 min (config) | Sincronização do catálogo Winget via clone de manifestos |
| `AlertSchedulerJob` | `alerts` | a cada 30s | Despacho de alertas agendados |
| `SlaMonitoringJob` | `alerts` | a cada 5 min | Verificação de SLA + escalações |
| `ReportGenerationJob` | `reports` | a cada 15s | Processamento de execuções de relatório |
| `ReportScheduleDispatchJob` | `reports` | a cada 60s | Despacho de execuções agendadas de relatórios |
| `SensorRollupJob` | `monitoring` | a cada 5 min | Agregação de amostras de sensores em rollups horários |
| `AgentLabelingReconciliationJob` | `reconciliation` | a cada 10 min | Reconciliação de labels de agentes |
| `AiTicketAssignmentJob` | `ai-assignment` | a cada 20s (config) | Fila de triagem por IA e fallback de chamados sem responsável |
| `TechnicianMetricsRefreshJob` | `ai-assignment` | tick 300s (config) | Snapshots de métricas por atendente para triagem por IA |
| `AiAssignmentLearningJob` | `ai-assignment` | `0 30 4 * * ?` (config) | Ciclo diário de aprendizado da triagem por IA |
| `TechnicianMetricsBackfillJob` | `ai-assignment` | a cada 30s (config) | Backfill em lotes dos snapshots de métricas |
| `PatchAutoInstallJob` | `patches` | a cada 15 min (config) | Instala patches aprovados dentro da janela de manutenção |
| `PatchRebootNoticeJob` | `patches` | a cada 15 min (config) | Política de aviso de reboot pendente (nunca reinicia) |
| `PatchMaintenanceJob` | `patches` | `0 15 4 * * ?` (config) | Expira instalações de patch sem resultado e reconcilia aprovações |
| `BackgroundProcessingScheduleSyncJob` | `maintenance` | a cada 120s (config) | Sincroniza o tick dos processamentos com a configuração global |

---

## Justificativa

### Por que Quartz.NET

- **Agendamento cron nativo** — jobs de manutenção usam cron, não `Task.Delay`
- **`[DisallowConcurrentExecution]`** — garante que jobs longos não sobreponham
- **Dashboard administrativo** — `GET /api/v1/admin/jobs` lista estado, histórico, triggers
- **Trigger manual** — `POST /api/v1/admin/jobs/{group}/{name}/trigger`
- **Pause/Resume** — `POST /api/v1/admin/jobs/{group}/{name}/pause`
- **Histórico de execução** — `JobExecutionHistoryListener` registra sucesso/erro/duração
- **Store in-memory** — suficiente para single-node; PostgreSQL via ADO.NET job store disponível quando necessário para clustering

### Por que NÃO Hangfire

- Licença paga para recursos como batches e continuations
- Menor adoção em .NET 10

---

## Arquitetura

```
QuartzServiceCollectionExtensions.AddDiscoveryQuartz()
  ├── services.AddQuartz(q => q.ScheduleJob<T>(...))   // 12 jobs
  ├── services.AddQuartzHostedService()                   // integração ASP.NET
  ├── services.AddSingleton<JobExecutionHistoryListener>() // métricas
  └── WireJobListenerAsync()                             // wiring no startup

Program.cs
  ├── builder.Services.AddDiscoveryQuartz(configuration)
  └── await WireJobListenerAsync(app.Services)

BackgroundServicesCollectionExtensions
  └── Apenas AgentPackagePrebuild + SyncPingDispatch (não migrados)
```

### Padrão dos jobs

```csharp
[DisallowConcurrentExecution]
public sealed class SeuJob : IJob
{
    public static readonly JobKey Key = new("nome", "grupo");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<SeuJob>();
        await using var scope = scopeFactory.CreateAsyncScope();
        // ... lógica do job
    }
}
```

---

## Configurações especiais

### WingetCatalogSyncJob

```json
{
  "BackgroundJobs": {
    "WingetCatalogSync": {
      "Enabled": false,
      "IntervalDays": 5
    }
  }
}
```

- `Enabled: false` por padrão — ativar manualmente
- `IntervalDays`: 1, 5, 10, 15, 30
- Se `Enabled = false`, o job não é registrado no scheduler

---

## Referências

- [Quartz.NET Documentation](https://www.quartz-scheduler.net/documentation/)
- [Quartz.NET ASP.NET Core Integration](https://www.quartz-scheduler.net/documentation/)
- Código: `src/Discovery.Api/DependencyInjection/QuartzServiceCollectionExtensions.cs`
- Código: `src/Discovery.Api/Services/Quartz/*.cs`
- API: `src/Discovery.Api/Controllers/JobsController.cs`

