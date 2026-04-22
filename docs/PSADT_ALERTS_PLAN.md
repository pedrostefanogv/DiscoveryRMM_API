# PSADT Agent Alerts — Plano de Implementação

## Visão Geral

Sistema de alertas exibidos no endpoint do agent via PSADT toolkit.  
O servidor define o alerta (tipo, conteúdo, escopo, agendamento) e o entrega ao agent usando o canal de comando existente (`AgentCommand` + `CommandType.ShowPsadtAlert`).

---

## Dois Tipos de Alerta

| Tipo | Comportamento | Timeout |
|------|---------------|---------|
| **Toast** | Fecha automaticamente | 5 / 15 / 30 segundos (padrão: 15) |
| **Modal** | Exige clique do usuário para fechar | Nenhum (ou ação padrão via `defaultAction`) |

---

## Escopos de Entrega

| Escopo | Campo necessário |
|--------|----------------|
| `Agent` | `ScopeAgentId` |
| `Site` | `ScopeSiteId` |
| `Client` | `ScopeClientId` |
| `Label` | `ScopeLabelName` (nome exato da label automática) |

---

## Canal de Entrega

Reutiliza o canal existente de comandos:

```
POST api/agent-alerts → cria AgentAlertDefinition

[Imediato]  → AlertDispatchService
               → resolve agentes pelo escopo
               → cria AgentCommand(CommandType.ShowPsadtAlert, payloadJson)
               → NATS: tenant.{c}.site.{s}.agent.{a}.command
               → SignalR: ExecuteCommand (se agent online)

[Agendado]  → AlertSchedulerBackgroundService (loop 30s)
               → detecta ScheduledAt <= UtcNow
               → AlertDispatchService.DispatchAsync

Agent offline → reconecta → RegisterAgent entrega pending commands automaticamente

Agent responde → CommandResult(cmdId, exitCode=0, output={"action":"yes"})
```

---

## Payload JSON enviado ao Agent

### Toast
```json
{
  "alertId": "guid",
  "type": "toast",
  "title": "Manutenção programada",
  "message": "O sistema reiniciará em 30 minutos.",
  "timeoutSeconds": 15,
  "icon": "warning"
}
```

### Modal
```json
{
  "alertId": "guid",
  "type": "modal",
  "title": "Confirmação necessária",
  "message": "Deseja reiniciar agora?",
  "actions": [
    { "label": "Sim", "value": "yes" },
    { "label": "Não", "value": "no" }
  ],
  "defaultAction": "no",
  "icon": "question"
}
```

---

## API Endpoints

| Método | Rota | Descrição |
|--------|------|-----------|
| `GET` | `api/agent-alerts` | Listar com filtros (status, scope, clientId, siteId, agentId, ticketId) |
| `GET` | `api/agent-alerts/{id}` | Detalhes de um alerta |
| `POST` | `api/agent-alerts` | Criar + despachar (ou agendar) |
| `POST` | `api/agent-alerts/{id}/dispatch` | Despacho manual imediato |
| `DELETE` | `api/agent-alerts/{id}` | Cancelar (somente se não despachado) |

### Exemplo: criar Toast imediato para um site
```json
POST /api/agent-alerts
{
  "title": "Atualização disponível",
  "message": "Uma nova versão está pronta para instalar.",
  "alertType": 0,
  "timeoutSeconds": 15,
  "icon": "info",
  "scopeType": 1,
  "scopeSiteId": "00000000-0000-0000-0000-000000000000"
}
```

### Exemplo: criar Modal agendado para um cliente
```json
POST /api/agent-alerts
{
  "title": "Reinicialização necessária",
  "message": "Seu computador será reiniciado em breve.",
  "alertType": 1,
  "actionsJson": "[{\"label\":\"Reiniciar agora\",\"value\":\"now\"},{\"label\":\"Depois\",\"value\":\"later\"}]",
  "defaultAction": "later",
  "icon": "warning",
  "scopeType": 2,
  "scopeClientId": "00000000-0000-0000-0000-000000000000",
  "scheduledAt": "2026-04-16T08:00:00Z",
  "expiresAt": "2026-04-16T09:00:00Z"
}
```

---

## Arquivos Implementados

### Core Domain
- `src/Discovery.Core/Enums/CommandType.cs` — adicionado `ShowPsadtAlert = 9`
- `src/Discovery.Core/Enums/PsadtAlertType.cs` — `Toast = 0 | Modal = 1`
- `src/Discovery.Core/Enums/AlertScopeType.cs` — `Agent | Site | Client | Label`
- `src/Discovery.Core/Enums/AlertDefinitionStatus.cs` — `Draft | Scheduled | Dispatching | Dispatched | Expired | Cancelled`
- `src/Discovery.Core/Entities/AgentAlertDefinition.cs` — entidade completa
- `src/Discovery.Core/Interfaces/IAgentAlertRepository.cs`
- `src/Discovery.Core/Interfaces/IAgentAlertService.cs` + `CreateAgentAlertRequest`

### Infrastructure
- `src/Discovery.Infrastructure/Repositories/AgentAlertRepository.cs` *(auto-registrado)*
- `src/Discovery.Infrastructure/Services/AgentAlertService.cs` *(auto-registrado)*
- `src/Discovery.Infrastructure/Data/DiscoveryDbContext.cs` — `DbSet<AgentAlertDefinition>` + mapping snake_case
- `src/Discovery.Migrations/Migrations/M095_CreateAgentAlertDefinitions.cs`

### API Layer
- `src/Discovery.Api/Services/AlertDispatchService.cs`
- `src/Discovery.Api/Services/AlertSchedulerBackgroundService.cs`
- `src/Discovery.Api/Controllers/AgentAlertsController.cs`
- `src/Discovery.Api/Program.cs` — registro de `AlertDispatchService` e `AlertSchedulerBackgroundService`

---

## Banco de Dados

Tabela: `agent_alert_definitions`

| Coluna | Tipo | Descrição |
|--------|------|-----------|
| `id` | uuid PK | |
| `title` | varchar(200) | |
| `message` | varchar(2000) | |
| `alert_type` | int | 0=Toast, 1=Modal |
| `timeout_seconds` | int? | Para Toast |
| `actions_json` | jsonb? | Botões para Modal |
| `default_action` | varchar(100)? | Ação automática no timeout |
| `icon` | varchar(50) | info/warning/error/success |
| `scope_type` | int | 0=Agent, 1=Site, 2=Client, 3=Label |
| `scope_agent_id` | uuid? | |
| `scope_site_id` | uuid? | |
| `scope_client_id` | uuid? | |
| `scope_label_name` | varchar(120)? | |
| `status` | int | 0=Draft..5=Cancelled |
| `scheduled_at` | timestamptz? | Nulo = imediato |
| `expires_at` | timestamptz? | Expirar se não despachado |
| `dispatched_at` | timestamptz? | |
| `dispatched_count` | int | Agents alcançados |
| `ticket_id` | uuid? | Vínculo com ticket |
| `created_by` | varchar(256)? | |
| `created_at` | timestamptz | |
| `updated_at` | timestamptz | |

---

## Ciclo de Vida do Status

```
Draft ──► Dispatching ──► Dispatched
  │
  └──► Scheduled ──► Dispatching ──► Dispatched
         │
         └──► Expired (ExpiresAt ultrapassado)

Draft/Scheduled ──► Cancelled (cancelamento manual)
```

---

## Integração com Ticket

O campo `TicketId` é opcional e permite vincular um alerta a um ticket de suporte para rastreabilidade no histórico.

---

## Considerações para o Agent

O agent recebe o comando `ShowPsadtAlert = 9` via canal existente (`ExecuteCommand`).  
O payload JSON contém tudo necessário para o PSADT renderizar o diálogo.  
A resposta é enviada de volta como `CommandResult(commandId, exitCode=0, output="{\"action\":\"yes\"}")`.

**Nenhuma alteração foi feita no código do agent.**
