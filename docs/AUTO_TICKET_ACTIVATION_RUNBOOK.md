# Runbook — Ativação do Auto-Ticket (Fase 8)

Este documento descreve como ligar o auto-ticket com segurança, partindo do
estado atual (**desligado**) até um canário real.

> **Estado atual:** `AutoTicket:Enabled = false` em `appsettings.json`.
> Nenhum chamado é criado automaticamente. As Fases 1–7 estão implementadas e
> testadas (API 2322 / Site 1522 testes, 0 falhas).

---

## 0. Onde ligar (pela interface)

Existem **duas telas**, porque são duas coisas diferentes:

### 0.1 Política — "o auto-ticket opera neste escopo?"

**Menu → Chamados → Auto-Ticket (Política)** (`/tickets/auto-policy`)

- Seletor de **Cliente** e **Site**. Deixando ambos vazios, edita o **global**.
- Mostra o **valor efetivo** de cada campo e **de onde ele veio**
  (`Server` / `Client` / `Site` / `Locked`) — a proveniência.
- **Kill switch por escopo:** marque *Habilitado = Não* no cliente (ou site) e
  só aquele escopo é desligado. Os clientes irmãos continuam funcionando.
- Campos deixados em branco **herdam** do nível superior; preencher sobrescreve
  apenas naquele nível.
- Vínculo de sensores por escopo: `allowedSensorKeys` e `neverTicketSensorKeys`.
- IA por escopo: modo (`Off`/`Suggest`/`AutoApply`) e se pode receber
  contexto do equipamento.

### 0.2 Regras — "qual alerta abre chamado, e como?"

**Menu → Chamados → Auto-Ticket (Regras)** (`/tickets/auto-rules`)

- CRUD das regras (escopo, filtro de alerta, prioridade, categoria, dedup).
- **Simular (dry-run):** mostra se criaria chamado e por quê, **sem gravar nada**.
- **Semear regras padrão:** conjunto conservador, idempotente.
- **Estatísticas:** série de decisões (criados / deduplicados / suprimidos /
  limitados / falhas) e as taxas.

### 0.3 Por sensor — "este sensor pode abrir chamado?"

**Menu → Sensores → (editar sensor) → seção "Auto-ticket"**

- **Pode abrir chamado?** `Nunca` / `Só na abertura/escala` / `Sempre`.
  "`Nunca`" **vence** as listas da política, mesmo que o sensor esteja nelas.
- **Impacto** (Baixo/Médio/Alto/Crítico): funciona como piso da prioridade
  sugerida pela IA.
- **Duração mínima em falha (s):** vazio ou 0 = herda a política hierárquica.

**Precedência final:**
`Sensor (Never)` → `Site` → `Cliente` → `Global` → `appsettings`.

---

## 1. Pré-requisitos (verificar ANTES de ligar)

### 1.1 Migrations aplicadas

As migrations são aplicadas por `runner.MigrateUp()` no startup da API
(`Program.cs:398`). Confirme que os três objetos existem:

| Migration | Objeto | Verificação |
|---|---|---|
| M267 | `auto_ticket_policy_json` em `server_configurations`, `client_configurations`, `site_configurations` | `\d server_configurations` deve listar a coluna |
| M268 | `auto_ticket_policy`, `auto_ticket_min_duration_seconds`, `auto_ticket_impact` em `sensor_definitions` | `\d sensor_definitions` |
| M269 | tabela `auto_ticket_suggestions` | `\d auto_ticket_suggestions` |

SQL de conferência:

```sql
SELECT table_name, column_name, data_type
FROM information_schema.columns
WHERE column_name IN ('auto_ticket_policy_json','auto_ticket_policy',
                      'auto_ticket_min_duration_seconds','auto_ticket_impact')
ORDER BY table_name, column_name;

SELECT to_regclass('public.auto_ticket_suggestions') AS suggestions_table;

SELECT version, description FROM versioninfo ORDER BY version DESC LIMIT 5;
```

### 1.2 Workflow com estado FINAL por cliente

**Isto é bloqueante.** O `AutoTicketResolutionService` fecha o chamado movendo-o
para o primeiro estado com `IsFinal = true` do cliente. Sem estado final (nem um
global com `client_id IS NULL`), o chamado **permanece aberto** — por design:
melhor deixar aberto do que mover para um estado inválido.

```sql
-- Clientes SEM estado final (nem próprio, nem global)
SELECT c.id, c.name
FROM clients c
WHERE NOT EXISTS (
  SELECT 1 FROM workflow_states w
  WHERE w.is_final = true AND (w.client_id = c.id OR w.client_id IS NULL)
);

-- Existe ao menos um estado final global?
SELECT count(*) FROM workflow_states WHERE is_final = true AND client_id IS NULL;
```

Se a primeira consulta retornar clientes, **crie um estado final** antes de
ligar para eles.

### 1.3 Blob de sensores

`SensorIngestionService` só chama o auto-ticket se
`sensors_settings_json.autoTicketEnabled` for true. O default do código é
`true`, mas o dado persistido pode divergir:

```sql
SELECT id, sensors_settings_json FROM server_configurations LIMIT 1;
```

### 1.4 Ao menos uma regra ativa

**Sem regra, nada dispara** — a cadeia é: regra casa → política permite → sinal
aprova → cria. Use `POST /api/v1/auto-ticket-rules/seed-defaults` ou crie uma
regra pela tela.

```sql
SELECT id, name, is_enabled, scope_level, alert_code_filter
FROM auto_ticket_rules WHERE is_enabled = true;
```

---

## 2. Sequência de ativação

Recomendado fazer em **dois passos**, com observação entre eles.

### Passo 1 — Shadow (observa, NÃO cria)

Objetivo: coletar "o que teria acontecido" sem abrir nenhum chamado.

```sql
-- Política global em Shadow.
UPDATE server_configurations
SET auto_ticket_policy_json = '{"enabled":true,"mode":1}'::jsonb;
```

(`mode`: 0=Off, 1=Shadow, 2=Canary, 3=Active)

Deixe rodar por alguns dias. Depois, meça:

```sql
SELECT decision, count(*), min(reason), max(reason)
FROM auto_ticket_rule_executions
WHERE evaluated_at > now() - interval '3 days'
GROUP BY decision ORDER BY 2 DESC;
```

**Critério para avançar:** a proporção `Created` (gravada em shadow como
"teria criado") deve ser razoável, e as supressões devem ter motivos legíveis
(`Maintenance`, `Dependency`, `Flapping`, `NotActionable`).

O veredito do sinal fica auditável no `payload_snapshot_json`:

```sql
SELECT payload_snapshot_json -> 'signal' AS signal_verdict, count(*)
FROM auto_ticket_rule_executions
WHERE evaluated_at > now() - interval '3 days'
  AND payload_snapshot_json ? 'signal'
GROUP BY 1 ORDER BY 2 DESC LIMIT 20;
```

### Passo 2 — Canário (cria, escopo restrito)

```sql
-- Canário para UM cliente. mode=2 (Canary); a lista restringe o escopo.
UPDATE server_configurations
SET auto_ticket_policy_json =
  '{"enabled":true,"mode":2,"canaryClientIds":["<CLIENT_ID>"],"maxCreatedTicketsPerHourPerAlertCode":5}'::jsonb;
```

Ou **só para o cliente** (sem tocar no global) — assim os outros ficam intactos:

```sql
UPDATE client_configurations
SET auto_ticket_policy_json = '{"mode":2,"canaryClientIds":["<CLIENT_ID>"]}'::jsonb
WHERE client_id = '<CLIENT_ID>';
```

**Observação importante:** a política é herdada campo a campo. O blob acima
sobrescreve `mode` e `canaryClientIds`; o resto continua vindo do global.

### Passo 3 — Ampliar

Remova o `canaryClientIds` ou mude para `mode = 3` (Active) quando estiver
confortável. Desligar um cliente específico é imediato:

```sql
-- Desliga SÓ este cliente (os demais não são afetados).
UPDATE client_configurations
SET auto_ticket_policy_json = '{"enabled":false}'::jsonb
WHERE client_id = '<CLIENT_ID>';

-- Ou por site.
UPDATE site_configurations
SET auto_ticket_policy_json = '{"enabled":false}'::jsonb
WHERE site_id = '<SITE_ID>';
```

O motivo registrado na execução passa a citar **qual nível desligou**
(ex.: `"AutoTicket is disabled at Site level."`), o que torna o diagnóstico
direto.

---

## 3. Verificação pós-ativação

```sql
-- Últimas decisões, com o nível que desligou (quando houver).
SELECT evaluated_at, decision, reason
FROM auto_ticket_rule_executions
ORDER BY evaluated_at DESC LIMIT 20;

-- Chamados criados automaticamente.
SELECT id, title, priority, category, created_at, closed_at
FROM tickets
WHERE id IN (SELECT created_ticket_id FROM auto_ticket_rule_executions
             WHERE created_ticket_id IS NOT NULL)
ORDER BY created_at DESC LIMIT 20;

-- Sugestões da IA: aplicadas vs recusadas (taxa de override).
SELECT field, applied, count(*)
FROM auto_ticket_suggestions
WHERE created_at > now() - interval '7 days'
GROUP BY field, applied ORDER BY field, applied;
```

### Endpoints úteis

| Endpoint | Para quê |
|---|---|
| `GET /api/v1/auto-ticket-policy?clientId=&siteId=` | Política efetiva + **de onde veio cada campo** |
| `POST /api/v1/auto-ticket-rules/simulate` | "O que aconteceria" sem gravar nada |
| `GET /api/v1/auto-ticket-rules/{id}/stats` | Série de decisões da regra |
| `POST /api/v1/auto-ticket-rules/seed-defaults` | Regras padrão conservadoras (idempotente) |

---

## 4. Rollback

O rollback é **imediato e não destrutivo** (nenhuma migration é revertida):

```sql
-- Desliga globalmente.
UPDATE server_configurations
SET auto_ticket_policy_json = '{"enabled":false}'::jsonb;

-- Ou use o kill switch por escopo (afeta só aquele cliente/site).
```

Chamados já criados **permanecem** — são registros legítimos. Se quiser
encerrá-los, faça pelo fluxo normal de tickets.

Para reverter o schema (se realmente necessário):

```bash
# Reverte M269, M268 e M267, nesta ordem.
dotnet fm migrate -p Postgres -c "<conn>" -a src/Discovery.Migrations/bin/Debug/net10.0/Discovery.Migrations.dll down --version 20270101_266
```

---

## 5. Riscos conhecidos e mitigações

| Risco | Mitigação já implementada |
|---|---|
| Enxurrada de chamados | `maxCreatedTicketsPerHourPerAlertCode` + dedup por correlação |
| Chamado órfão (abre na manutenção e nunca fecha) | `closeTicketResolvedDuringMaintenance` (default true) |
| Supressão permanente pelo que foi silenciado na manutenção | `reNotifyAfterMaintenanceEnds` (default true) |
| Chamado para sensor ruidoso | `neverTicketSensorKeys` + `SensorDefinition.AutoTicketPolicy = Never` |
| IA rebaixar incidente crítico | **Piso de prioridade** em escalação |
| IA rotear para outro cliente | Validação de vínculo departamento↔cliente |
| IA alucinar | Fatos vêm da evidência coletada; fallback determinístico |
| IA indisponível | Timeout + circuito + texto determinístico; **nunca** derruba a criação |
| Silêncio de agente virar "resolvido" | Só `transition = resolved` fecha |

---

## 6. O que NÃO foi validado

- **As migrations não foram executadas contra o Postgres real** — o host
  `192.168.1.21:5432` esteve inacessível durante todo o desenvolvimento e não
  havia Docker. Os DDLs foram executados contra SQLite (prova de que os comandos
  são bem-formados e que o `Down` reverte), e há testes de integridade que
  garantem unicidade e ordenação das versões. **O dialeto `jsonb` só será
  exercitado de fato no banco real.**
- **O conteúdo de `sensors_settings_json` não foi inspecionado** (banco
  inacessível). Se `autoTicketEnabled` estiver `false` lá, nada dispara.
- `MaintenanceWindowJson` é string livre sem schema validado. O avaliador faz
  *fail-open*: formato inválido **não** suprime, apenas registra warning.
