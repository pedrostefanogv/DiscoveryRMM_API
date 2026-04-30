# ADR: Background Jobs — Quartz.NET

> **Status:** Implementado ✅  
> **Data:** 2026-04-29  
> **Decisores:** Time DiscoveryRMM  

---

## Decisão

Migrar todos os jobs agendados e recorrentes de `IHostedService` / `BackgroundService` para **Quartz.NET 3.14.0** com store in-memory, `[DisallowConcurrentExecution]` em todos os jobs, e dashboard administrativo via `JobsController`.

Três serviços foram mantidos como `IHostedService` por design: `AgentPackagePrebuildHostedService` (one-time startup), `SyncPingDispatchBackgroundService` (padrão singleton com estado interno) e `AlertDispatchService` (scoped, não é background).

---

## Jobs Quartz implementados

| Job | Grupo | Schedule | Descrição |
|-----|-------|----------|-----------|
| `LogPurgeJob` | `maintenance` | `0 0 3 * * ?` (3AM) | Purge de logs antigos |
| `ReportRetentionJob` | `maintenance` | `0 0 4 * * ?` (4AM) | Purge de relatórios expirados |
| `AiChatRetentionJob` | `maintenance` | `0 0 2 * * ?` (2AM) | Soft/hard delete de chat sessions |
| `P2pMaintenanceJob` | `maintenance` | a cada 15 min | Limpeza de P2P presence + seed plans |
| `KnowledgeEmbeddingJob` | `kb` | a cada 30s | Re-chunking + embeddings em batch |
| `AlertSchedulerJob` | `alerts` | a cada 30s | Despacho de alertas agendados |
| `SlaMonitoringJob` | `alerts` | a cada 5 min | Verificação de SLA + escalações |
| `ReportGenerationJob` | `reports` | a cada 15s | Processamento de execuções de relatório |
| `AgentLabelingReconciliationJob` | `reconciliation` | a cada 10 min | Reconciliação de labels de agentes |
| `MeshCentralIdentityReconciliationJob` | `reconciliation` | `0 0 * * * ?` (horário) | Backfill de identidade MeshCentral |
| `MeshCentralGroupPolicyReconciliationJob` | `reconciliation` | `0 30 * * * ?` (hora:30) | Backfill de group policy MeshCentral |
| `WingetCatalogSyncJob` | `catalog` | a cada N dias (config) | Sincronização do catálogo Winget |

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

- [Quartz.NET Documentation](https://www.quartz-scheduler.net/)
- [Quartz.NET ASP.NET Core Integration](https://www.quartz-scheduler.net/documentation/quartz-3.x/packages/hosted-services-integration.html)
- Código: `src/Discovery.Api/DependencyInjection/QuartzServiceCollectionExtensions.cs`
- Código: `src/Discovery.Api/Services/Quartz/*.cs`
- API: `src/Discovery.Api/Controllers/JobsController.cs`

