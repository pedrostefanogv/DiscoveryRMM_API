# ADR: Estratégia de Background Jobs — Quartz.NET vs IHostedService

> **Status:** Fase 1 e 2 implementadas ✅  
> **Data:** 2026-04-29  
> **Decisores:** Time DiscoveryRMM  

---

## Contexto

Atualmente o projeto usa `IHostedService` + `BackgroundService` para todos os jobs, com toggles via `appsettings.json` (`BackgroundJobs:*Enabled`). São **15+ serviços** registrados manualmente.

### Serviços atuais

| Serviço | Tipo | Complexidade |
|---------|------|-------------|
| `AlertSchedulerBackgroundService` | Agendamento recorrente | Alta — precisa de cron |
| `SlaMonitoringBackgroundService` | Polling periódico | Média |
| `ReportGenerationBackgroundService` | Job sob demanda | Média |
| `ReportRetentionBackgroundService` | Limpeza agendada | Baixa |
| `AiChatRetentionBackgroundService` | Limpeza agendada | Baixa |
| `LogPurgeBackgroundService` | Limpeza agendada | Baixa |
| `P2pMaintenanceBackgroundService` | Manutenção periódica | Baixa |
| `KnowledgeEmbeddingBackgroundService` | Processamento contínuo | Alta |
| `KnowledgeEmbeddingQueueBackgroundService` | Fila dedicada | Alta |
| Reconciliations (3 serviços) | Sync agendado | Média |
| `AgentPackagePrebuildHostedService` | One-time na startup | Baixa |

### Limitações do modelo atual

1. **Sem retry nativo** — falhas silenciosas
2. **Sem dashboard** — impossível saber estado dos jobs sem logs
3. **Sem agendamento cron** — apenas loops com `Task.Delay`
4. **Sem execução distribuída** — scaling horizontal pode duplicar jobs
5. **Sem pausa/retomada** — impossível pausar um job específico

---

## Alternativas Consideradas

### A) Manter IHostedService (atual)

**Prós:** Nenhuma dependência nova, simples.  
**Contras:** Todas as limitações acima.

### B) Quartz.NET

**Prós:**
- Agendamento cron nativo
- Persistência em PostgreSQL (via `Quartz.Serialization.Json` + ADO.NET job store)
- Dashboard via API (expor `/quartz/dashboard` ou usar UI standalone)
- Clustering para execução distribuída
- Retry policy configurável
- Suporte a misfire handling
- Comunidade ativa, maduro

**Contras:**
- +1 dependência
- Curva de aprendizado
- Migração dos `IHostedService` existentes

### C) Hangfire

**Prós:** Dashboard integrado, simples.  
**Contras:** Licença paga para recursos avançados (batches, continuations). Pouco adotado em .NET 10.

---

## Recomendação: **Quartz.NET** (Alternativa B)

### Plano de Migração (Faseado)

#### Fase 1 — Infraestrutura (baixo risco)
- Adicionar pacotes `Quartz`, `Quartz.Extensions.Hosting`, `Quartz.Serialization.Json`
- Configurar `AddQuartz()` no `Program.cs` com store PostgreSQL
- Criar `QuartzServiceCollectionExtensions.cs` na pasta `DependencyInjection/`

#### Fase 2 — Jobs simples primeiro
Migrar os 4 jobs agendados de menor complexidade:
1. `LogPurgeBackgroundService` → `LogPurgeJob : IJob`
2. `ReportRetentionBackgroundService` → `ReportRetentionJob : IJob`
3. `AiChatRetentionBackgroundService` → `AiChatRetentionJob : IJob`
4. `P2pMaintenanceBackgroundService` → `P2pMaintenanceJob : IJob`

#### Fase 3 — Jobs complexos
5. `AlertSchedulerBackgroundService` → `AlertSchedulerJob : IJob` (com cron `0 */5 * * * ?`)
6. `SlaMonitoringBackgroundService` → `SlaMonitoringJob : IJob`
7. Reconciliations → jobs independentes com cron

#### Fase 4 — Jobs contínuos (avaliação)
`KnowledgeEmbedding*` pode não se beneficiar de Quartz (são loops contínuos). Manter como `BackgroundService` ou avaliar Channels.

### Configuração proposta

```csharp
// QuartzServiceCollectionExtensions.cs
services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        s.UsePostgres(connectionString);
        s.UseJsonSerializer();
    });

    // Job schedules
    q.ScheduleJob<LogPurgeJob>(trigger => trigger
        .WithIdentity("log-purge")
        .WithCronSchedule("0 0 3 * * ?")); // Diário às 3AM
});
```

### Feature flags

Manter os toggles `BackgroundJobs:*Enabled` do `appsettings.json` — o Quartz respeita `WithSchedule()` condicional.

---

## Decisão

- [x] **Aprovar** — Fase 1 e 2 implementadas ✅
- [ ] **Rejeitar** — descartado
- [ ] **Adiar** — descartado

### O que foi implementado

| Job Quartz | Cron / Schedule | Substitui |
|-----------|----------------|-----------|
| `LogPurgeJob` | `0 0 3 * * ?` (diário 3AM) | `LogPurgeBackgroundService` |
| `ReportRetentionJob` | `0 0 4 * * ?` (diário 4AM) | `ReportRetentionBackgroundService` |
| `AiChatRetentionJob` | `0 0 2 * * ?` (diário 2AM) | `AiChatRetentionBackgroundService` |
| `P2pMaintenanceJob` | cada 15 min | `P2pMaintenanceBackgroundService` |

**Arquitetura:** Quartz.NET 3.14.0 com store in-memory (Phase 1), `Quartz.Extensions.Hosting` para integração ASP.NET, `Quartz.Serialization.Json` para serialização. `[DisallowConcurrentExecution]` em todos os jobs.

---

## Referências

- [Quartz.NET Documentation](https://www.quartz-scheduler.net/)
- [Quartz.NET ASP.NET Core Integration](https://www.quartz-scheduler.net/documentation/quartz-3.x/packages/hosted-services-integration.html)
