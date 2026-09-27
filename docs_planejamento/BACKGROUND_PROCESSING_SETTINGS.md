# Processamento em Segundo Plano Configurável

> Ciclos das **métricas por atendente** e da **triagem de chamados por IA**. Migração M181.

## 1. O que mudou

- Os dois processamentos deixaram de rodar "tudo de uma vez" e passaram a rodar em **ciclos por escopo de cliente**, em lotes, com teto de trabalho por execução.
- A configuração é **global com herança para o cliente** (o cliente sobrescreve apenas os campos que quiser).
- As **métricas não são mais calculadas dentro de requisição**: a leitura usa o snapshot materializado; snapshot vencido é atualizado pelo ciclo.
- O cálculo das métricas saiu da memória: agora é **agregação SQL** (uma linha por atendente em vez de todos os tickets do lote).
- A triagem continua com o caminho rápido na abertura do chamado (configurável) e ganhou **cota por cliente** no ciclo em lotes.

## 2. Configuração

Persistida em JSON: global em `server_configurations.background_processing_settings_json`,
override em `client_configurations.background_processing_settings_json` (campos ausentes herdam;
`null` = herdar). O campo é gerenciável como os demais: entra em `ConfigurationFieldCatalog.ManagedFields`
e pode ser **bloqueado** por `LockedFieldsJson` do cliente.

    Metrics:
      Enabled (true)                   liga/desliga o ciclo do escopo
      IntervalMinutes (15)             intervalo mínimo entre ciclos do mesmo cliente — PISO 10
      StaleThresholdMinutes (15)       a partir de quando o snapshot é considerado vencido
      WindowDays (90)                  janela das taxas e médias
      BatchSize (200)                  atendentes por lote
      MaxBatchesPerRun (4)             teto de lotes por execução
      MaxRunSeconds (120)              orçamento de tempo por execução
      BootstrapMissingSnapshots (true) calcula UMA vez quem nunca teve snapshot
    Triage:
      Enabled (true)
      EnqueueOnCreate (true)           true = fila na abertura (fast lane); false = somente lotes
      IntervalSeconds (20)             vencimento do lote por cliente (piso 10 s)
      BatchSize (25)                   itens por execução (limite global do processo)
      MaxPerClientPerRun (10)          cota por cliente (fairness)
      MaxAttempts (3)                  tentativas antes do fallback determinístico
      RetryAfterMinutes (5)            idade mínima para a varredura de segurança
      BatchDelaySeconds (0)            atraso no modo somente-lotes

Valores fora da faixa são ajustados (não descartados) — o ciclo nunca para por configuração inválida.

### Como configurar

- **Global**: Configurações do Servidor → campo "Processamento em Segundo Plano" (`PATCH /configurations/server`).
- **Por cliente**: editor de configuração do cliente → "Processamento em Segundo Plano (override)" (`PATCH /configurations/clients/{clientId}`).
  Basta enviar os campos que mudam; ex.: `{"Metrics":{"IntervalMinutes":60},"Triage":{"Enabled":false}}`.
- **Efetivo**: `GET /api/v1/configurations/background-processing/effective?clientId=` devolve o resultado do merge.
- **Status**: `GET /api/v1/configurations/background-processing/status?clientId=` devolve o último ciclo por escopo.

### Appsettings (kill switch e tick)

    BackgroundJobs:TechnicianMetrics:Enabled (default true)
    BackgroundJobs:TechnicianMetrics:TickSeconds (default 300, piso 60)
    BackgroundJobs:TechnicianMetrics:StartupDelaySeconds (default 60)
    BackgroundJobs:AiTicketAssignment:Enabled (default true)
    BackgroundJobs:AiTicketAssignment:IntervalSeconds (default 20, piso 10)
    BackgroundJobs:AiTicketAssignment:StartupDelaySeconds (default 20)
    BackgroundJobs:AiAssignmentLearning:Enabled|HourUtc

Decisão de desenho: o appsettings controla **se** o processo roda (kill switch) e `StartupDelaySeconds`;
o **tick** vem da configuração global do banco (campos `Metrics.TickSeconds` / `Triage.TickSeconds`) e é
**aplicado no Quartz em runtime**; o **intervalo efetivo por cliente** continua sendo verificado dentro do
ciclo. Assim é possível dar intervalos diferentes a clientes diferentes — e mudar o tick — sem reiniciar.

### Tick dinâmico (fase 4)

- `BackgroundProcessingScheduleService` lê o tick do banco, compara com o aplicado e só reprograma quando
  muda (`IScheduler.RescheduleJob`), respeitando o kill switch.
- Aplicação: no **startup** (`force: true`, alinha com o banco), **após salvar** a configuração do servidor
  (PATCH/PUT) e por um **job de sincronização** a cada 2 min (`BackgroundJobs:BackgroundProcessingScheduleSync:*`).
- `GET /api/v1/configurations/background-processing/schedule` devolve tick desejado, tick aplicado,
  kill switch e próximo disparo.
- O tick é global por decisão de desenho (um trigger é global) e **não** é sobrescrevível por cliente.

## 3. Agendamento: tick + vencimento por escopo

Tabela `processing_scope_state` (`scope_type`, `scope_id`, `last_run_at`, `last_result_json`) é a fonte
única do "o escopo já venceu?" e da observabilidade. Escopo global usa `scope_id = Guid.Empty`.

    tick (5 min) -> descobre escopos -> para cada escopo:
        vencido? (now - last_run_at >= Intervalo)  -> não: pula
        habilitado? (Enabled)                      -> não: pula
        processa até min(BatchSize, cota) itens
        grava last_run_at + resumo

## 4. Métricas por atendente

### 4.1 Agregação em SQL

`ITechnicianMetricsAggregationRepository` executa 4 consultas por lote: agregados por atendente
(`assigned_total` da janela, `resolved_total`, `open_now` de qualquer época, FRT médio, resolução média,
`percentile_cont(0.90)`, SLA violado, CSAT), top categorias (`ROW_NUMBER() <= 5`), reaberturas
(`ticket_activity_logs` do tipo Reopened) e dificuldade média (`ticket_assignment_decisions` aplicadas).

Índice novo: `ix_tickets_assigned_user_created` — parcial em `(assigned_to_user_id, created_at DESC)`
`WHERE deleted_at IS NULL AND assigned_to_user_id IS NOT NULL`. Sem ele a descoberta de alvos e a
agregação fariam varredura completa em `tickets`.

### 4.2 Cobertura por ciclos sucessivos

Alvos são `(cliente, atendente)` a partir de vínculos de departamento e de responsáveis por chamados.
Quem está mais vencido vem primeiro (`ComputedAt` ascendente, nulos primeiro), então cada execução
avança a cobertura sem tabela de fila. Exemplo: 4 lotes x 200 = 800 atendentes/ciclo; 10.000 atendentes
ficam cobertos em ~13 ciclos (~65 min com tick de 5 min). Para acelerar, aumente `BatchSize`/`MaxBatchesPerRun`.

### 4.3 Leitura

`GetMetricsAsync`/`GetMetricsForUsersAsync` devolvem o snapshot como está — **não recalculam** snapshot
vencido. Sem snapshot: com `BootstrapMissingSnapshots` calcula uma vez e grava; sem ele devolve métrica
neutra (`ComputedAt = null`). A triagem registra em `ticket_assignment_decisions.metrics_snapshot_age_minutes`
a idade do snapshot mais antigo usado na decisão.

## 5. Triagem por IA

- Escopos com itens pendentes (fila) e escopos com chamados AiTriage sem responsável entram no ciclo.
- Cota por cliente: `min(BatchSize, MaxPerClientPerRun)` itens por execução — um cliente com fila grande não
  consome a vez dos demais.
- `EnqueueOnCreate = false` (modo somente-lotes): a abertura **não** enfileira; o chamado é pego pela
  varredura após `BatchDelaySeconds`.
- Varredura de segurança: no modo com fila usa `RetryAfterMinutes`; no modo somente-lotes usa
  `BatchDelaySeconds`. Só age em departamentos AiTriage com modo **Automático** (no assistido o chamado
  espera confirmação humana).
- Falha do modelo: retry com backoff até `MaxAttempts`, depois fallback determinístico do departamento.

## 6. Observabilidade

- `context.Result` dos jobs (histórico de execução do Quartz) com escopos, snapshots, triagens e tempo.
- `processing_scope_state.last_result_json`: `{ updated, pending }` (métricas) e `{ triaged, swept }` (triagem).
- `metrics_snapshot_age_minutes` na decisão da triagem.

## 7. Testes

- `TechnicianMetricsServiceTests`: leitura sem recálculo, bootstrap ligado/desligado, vencimento por escopo,
  cliente desabilitado ignorado, teto de lotes e cobertura no ciclo seguinte.
- `AiTicketTriageServiceTests`: ciclo processa fila do escopo vencido, não processa escopo antes do intervalo,
  não atribui no modo assistido, varredura no modo automático.
- `TicketAutoAssignmentServiceTests`: `EnqueueOnCreate = false` não enfileira.
- `AiCostControlServiceTests`, `BackgroundProcessingSettings`/merger: faixas e herança campo a campo.

## 8. Card dedicado no console (fase 4)

- `BackgroundProcessingCard` em "Configurações do Servidor" (modo **global**) e no editor de configuração do
  **cliente** (modo **override**), com campos tipados, faixa (piso/teto) e ajuda por campo.
- No modo override cada campo mostra **"herdado do global: X"** e um botão para passar a sobrescrever;
  o JSON salvo contém **apenas** os campos sobrescritos (ausentes herdam).
- Painel **Últimos ciclos**: usa `GET /background-processing/status` e mostra, por escopo, o processo
  (métricas, triagem, backfill), quando rodou, o resumo (`updated/pending`, `triaged/swept`, `processed/total`)
  e o tick aplicado.
- Ações: "Rodar métricas agora" / "Rodar triagem agora" (`jobsApi.trigger`), "Backfill de snapshots" e
  "Cancelar backfill" quando há um em andamento.

## 9. Backfill de snapshots (fase 4)

Recálculo **forçado** da janela configurada, para todos os atendentes (decisão do usuário):

- `POST /api/v1/configurations/background-processing/backfill?clientId=&purgeOrphans=` (202) e
  `POST .../backfill/cancel` — pedido idempotente por escopo (não duplica enquanto pendente/em andamento).
- `TechnicianMetricsBackfillJob` (30 s) processa um lote por execução: `BatchSize x MaxBatchesPerRun` usuários,
  reusando a agregação SQL.
- Progresso em `processing_scope_state` (`scope_type = technician_metrics_backfill`):
  `{ status, requestedAt, requestedBy, total, processed, lastError, completedAt }`.
- A **sessão** (`SessionStartUtc`) funciona como cursor: `RefreshForcedAsync` recalcula quem foi calculado
  antes do início da sessão, então a operação é retomável e idempotente sem guardar a lista de usuários.
- `purgeOrphans=true` remove snapshots de usuários sem vínculo/chamados ao concluir um backfill global.
- O backfill roda mesmo com `Metrics.Enabled = false` (ação explícita de administrador).

## 10. Fora de escopo (próximas fases)

- Herança em nível de **site** (a estrutura suporta; hoje é Global → Cliente por decisão de produto).
- Backfill com janela histórica própria ou modo diagnóstico sem gravar.
- Store persistente do Quartz (hoje em memória; cada instância reprograma a si própria).
- Movimentação das consultas de reabertura/dificuldade para SQL (hoje por EF, custo baixo por lote).
