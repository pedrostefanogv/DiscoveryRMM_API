# Knowledge Embedding — Análise de Otimização

> **Status:** Análise  
> **Data:** 2026-04-29  

---

## Arquitetura Atual

Dois `BackgroundService` rodando em loop contínuo:

### 1. `KnowledgeEmbeddingBackgroundService` (polling)

- Loop a cada 30s (ativo) / 10min (idle) / 30min (desabilitado)
- 2 passos por ciclo: re-chunking → geração de embeddings
- Batch size: 20 artigos/chunks por ciclo
- Usa `ILogger` + `IServiceScopeFactory`

### 2. `KnowledgeEmbeddingQueueBackgroundService` (LISTEN/NOTIFY)

- Abre conexão PostgreSQL dedicada com `LISTEN knowledge_embedding_queue`
- Processa itens da tabela `knowledge_embedding_queue_items`
- Batch size: 5 itens por ciclo, max 5 tentativas de retry
- Fallback: polling a cada 10min se não houver notificação

---

## Problemas Identificados

| # | Problema | Impacto | Severidade |
|---|---------|---------|-----------|
| 1 | **Duas conexões concorrentes à API de embeddings** — os dois serviços podem estar chamando `GenerateEmbeddingAsync` simultaneamente, estourando rate limits | Rate limit, custos | 🔴 Alta |
| 2 | **Sem batching de embeddings** — cada chunk gera 1 chamada de API. 20 chunks = 20 HTTP calls sequenciais | Latência, custo | 🔴 Alta |
| 3 | **Sem paralelismo controlado** — chunks são processados sequencialmente | Latência | 🟠 Média |
| 4 | **Retry frágil** — falha em um chunk não interrompe o batch, mas marca erro sem backoff exponencial | Consistência | 🟠 Média |
| 5 | **Conexão PostgreSQL dedicada** — `LISTEN` ocupa uma conexão do pool permanentemente | Recursos | 🟡 Baixa |
| 6 | **Polling desnecessário quando vazio** — se não há artigos pendentes, ainda faz polling a cada 10min | CPU/DB | 🟡 Baixa |
| 7 | **Sem métricas de observabilidade** — não há contadores de chunks processados, latência média, taxa de erro | Debug | 🟡 Baixa |

---

## Otimizações Propostas

### 🥇 Prioridade 1: Unificar em um único job com semáforo

```csharp
[DisallowConcurrentExecution]
public sealed class KnowledgeEmbeddingJob : IJob
{
    private static readonly SemaphoreSlim _embeddingSemaphore = new(1, 1);

    public async Task Execute(IJobExecutionContext context)
    {
        if (!await _embeddingSemaphore.WaitAsync(TimeSpan.Zero))
        {
            // Já tem embedding rodando, pula este ciclo
            return;
        }
        try { /* chunking + embedding */ }
        finally { _embeddingSemaphore.Release(); }
    }
}
```

**Benefício:** Garante que só 1 worker de embedding esteja ativo por vez, eliminando concorrência na API de embeddings.

### 🥇 Prioridade 2: Batching de embeddings

Em vez de 1 chamada HTTP por chunk, usar a API de embeddings em batch:

```csharp
// OpenAI / OpenRouter suportam arrays no input
var response = await client.Embeddings.CreateAsync(new EmbeddingRequest
{
    Model = model,
    Input = chunks.Select(c => c.Content).ToArray() // batch de N chunks
});
```

**Benefício:** 20 chunks → 1 chamada HTTP (20x menos latência, reduz custo de rede).

### 🥈 Prioridade 3: Parallel.ForEachAsync com DegreeOfParallelism

```csharp
var parallelOptions = new ParallelOptions
{
    MaxDegreeOfParallelism = 3,
    CancellationToken = ct
};

await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, token) =>
{
    // Gera embedding individual (se API não suporta batch)
});
```

**Benefício:** 3x mais rápido no pior caso.

### 🥉 Prioridade 4: Quartz para scheduling + trigger manual

Migrar de `BackgroundService` loop para Quartz com:
- **Trigger principal:** a cada 30s (`WithSimpleSchedule`)
- **Trigger manual:** endpoint REST para forçar execução imediata
- **Listener:** para métricas de execução (duração, erros, chunks processados)

### 🥉 Prioridade 5: Observabilidade

Adicionar métricas:
```csharp
// Meter + Counter
knowledgeEmbeddingCounter.Add(chunksProcessed);
knowledgeEmbeddingLatency.Record(sw.ElapsedMilliseconds);
knowledgeEmbeddingErrors.Add(1, tag);
```

---

## Recomendação Final

### Curto prazo (esta sprint)
1. Unificar 2 serviços em 1 `KnowledgeEmbeddingJob : IJob`
2. Adicionar semáforo anti-concorrência
3. Migrar para Quartz com trigger manual

### Médio prazo (próxima sprint)
4. Batching de embeddings (investigar se o provider suporta)
5. Parallel.ForEachAsync com DOP=3
6. Métricas de observabilidade

### Trade-off: Quartz vs BackgroundService para loops contínuos

| Aspecto | BackgroundService | Quartz |
|---------|------------------|--------|
| Loop contínuo | ✅ Natural | ⚠️ Precisa de trigger repetitivo |
| Trigger manual | ❌ Precisa de Channel/Semaphore custom | ✅ `scheduler.TriggerJob()` |
| Retry | ❌ Manual | ✅ `[PersistJobDataAfterExecution]` + `JobExecutionException` |
| Métricas | ❌ Manual | ✅ `IJobListener` / `ITriggerListener` |
| Dashboard | ❌ | ✅ `IScheduler` expõe estado de todos os jobs |

**Conclusão:** Mesmo para loops contínuos, Quartz oferece vantagens de gerenciamento. A migração é recomendada.
