# Análise de Permissão de Chamados por Departamento/Time

**Projeto:** DiscoveryRMM_API (backend .NET 10 + PostgreSQL)
**Pergunta:** o sistema HOJE atende ao requisito de que cada departamento tenha seu TIME
de atendimento, que só membros desse time tratem/atendam os chamados do departamento,
e que os demais usuários (e agentes remotos) só fechem o chamado que eles mesmos abriram?

**Resposta curta:** **NÃO.** O modelo de autorização atual é exclusivamente
**Cliente/Site**. Não existe nenhum conceito de autorização por departamento nem de
"fechar só o próprio chamado" no backend. O vínculo `DepartmentMember` existe, mas é
usado **apenas para auto-atribuição** (round-robin / least-open / triagem por IA) — nunca
para decidir se alguém pode tratar um chamado.

**Modo da análise:** somente leitura. Nenhum arquivo de código foi alterado.
Todo achado abaixo foi verificado no código indexado (`RMM-API`) e confirmado por leitura
do arquivo exato. Onde não houve verificação, está escrito **"não verificado"**.

---

## 1. Modelo atual de autorização

### 1.1 Enumeração de recursos e ações

| Item | Localização | Conteúdo |
| --- | --- | --- |
| `ResourceType` | `src/Discovery.Core/Enums/Identity/ResourceType.cs:3-36` | 21 recursos. Existe **`Departments`** (linha 26), mas **não existe** `DepartmentTickets`, `TicketQueue` nem `TicketTeam`. |
| `ActionType` | `src/Discovery.Core/Enums/Identity/ActionType.cs:3-10` | `View, Create, Edit, Delete, Execute`. **Não existe** ação `Close`, `Attend`, `Treat` nem `Assign`. |
| `ScopeLevel` | `src/Discovery.Core/Enums/Identity/ScopeLevel.cs:9-14` | **Somente** `Global, Client, Site`. **Não existe** `Department`. |
| `ScopeSource` | `src/Discovery.Core/Enums/Identity/ScopeSource.cs:6-19` | `Global, FromRoute, AccessList`. |

Ponto central: **o eixo de escopo do sistema termina em Site.** O departamento não é um
nível de escopo em lugar nenhum da hierarquia de autorização.

### 1.2 PermissionService

`src/Discovery.Infrastructure/Services/PermissionService.cs`

- `HasPermissionAsync` (linhas 29-49): percorre as atribuições de role do usuário e
  aprova se `IsScopeMatch` passar **e** houver um `Permission` com
  `(ResourceType, ActionType)` iguais.
- `IsScopeMatch` (linhas 170-195): trata apenas três casos — `Global` (linha 177-178,
  aprova sempre), `Client` (linhas 180-186) e `Site` (linhas 188-192). **Não há ramo
  para departamento**; um escopo de departamento simplesmente cairia no `return false`
  da linha 194.
- `GetScopeAccessAsync` (linhas 51-84): devolve `UserScopeAccess` com
  `HasGlobalAccess`, `AllowedClientIds` e `AllowedSiteIds`. **Não há coleção de
  departamentos permitidos.**

`UserScopeAccess` (`src/Discovery.Core/Interfaces/Auth/IPermissionService.cs:42-47`)
tem exatamente três campos: `HasGlobalAccess`, `AllowedClientIds`, `AllowedSiteIds`.

### 1.3 ScopeContext

`src/Discovery.Infrastructure/Services/ScopeContext.cs`

- Cache intra-request por `(UserId, Resource, Action)` (linha 32).
- Expoõe `ResolvedClientId` e `ResolvedSiteId` (linhas 34-35) — **não existe
  `ResolvedDepartmentId`.**
- `GetAccessAsync` (linhas 50-62) delega ao `PermissionService`.

### 1.4 RequirePermissionAttribute

`src/Discovery.Api/Filters/AuthFilters.cs:187-216` (atributo) e `:218-381` (filtro).

- `ResolveScopeFromRoute` (linhas 327-364) resolve escopo **apenas** a partir de
  `siteId`, `clientId` e, heuristicamente, `id` para `ClientsController`/`SitesController`
  (linhas 355-356).
- O fallback é `ScopeLevel.Global` (linha 363).
- **Não há nenhuma chave de rota `departmentId` reconhecida**, nem heurística para
  controllers de chamado.
- O filtro grava em `HttpContext.Items["ClientId"]`/`["SiteId"]` (linhas 307-308) —
  nunca um `DepartmentId`.

### 1.5 TicketAccessGuard e TicketsController.CanAccessTicketAsync

Existem **duas implementações duplicadas da mesma regra**:

`src/Discovery.Infrastructure/Services/TicketAccessGuard.cs:16-31`:

```csharp
var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, action);
if (access.HasGlobalAccess) return true;
var ticket = await ticketQueryService.GetTicketByIdAsync(ticketId, ct);
if (ticket is null) return false;
return access.AllowedClientIds.Contains(ticket.ClientId)
    || (ticket.SiteId.HasValue && access.AllowedSiteIds.Contains(ticket.SiteId.Value));
```

`src/Discovery.Api/Controllers/TicketsController.cs:48-61` — mesma lógica, copiada.

Consequência direta: a decisão compara **apenas `ticket.ClientId` e `ticket.SiteId`.**
O `ticket.DepartmentId` **nunca é lido** nessa decisão. Qualquer usuário com
`Tickets.Edit` no cliente/site do chamado pode editar, transicionar, mesclar, comentar e
fechar **qualquer** chamado daquele cliente/site, independente do departamento e
independente de quem abriu.

### 1.6 Prova negativa (busca exaustiva)

Buscas feitas no código-fonte (`src/**/*.cs`, excluindo testes e migrations):

| Padrão buscado | Ocorrências |
| --- | --- |
| `IsMemberOf` / `IsDepartmentMember` / `CanTreatTicket` / `CanAttendTicket` / `CanCloseTicket` / `TeamMembership` | **0** |
| `RequesterUserId ==` (comparação de solicitante para autorizar) | **0** |
| Referências a `Department`/`DepartmentMember` em `AuthFilters.cs` | **0** |
| Referências a `Department`/`DepartmentId` em `TicketWorkflowService.cs` | **0** |

Isso é decisivo: **não existe hoje nenhuma autorização baseada em departamento, em
vínculo de time, ou em autoria do chamado.** O que existe é RBAC por recurso
(`Tickets.View/Create/Edit/Delete/Execute`) com escopo Global/Client/Site.

---

## 2. Departamento / Time: o que existe hoje

### 2.1 Entidades

| Entidade | Localização | Observação |
| --- | --- | --- |
| `Department` | `src/Discovery.Core/Entities/Department.cs:8-165` | `ClientId` nulo = departamento **global** (linhas 13-15); `InheritFromGlobalId` para herança de um salto (linhas 20-24); `IsActive` (linha 27); toda a configuração de `AssignmentStrategy`/triagem por IA (linhas 29-90). **Não há flag de permissão.** |
| `DepartmentMember` | `src/Discovery.Core/Entities/DepartmentMember.cs:4-28` | `DepartmentId`, `UserId`, `IsActive`, `CreatedAt` + campos de **perfil de triagem** (`SkillTagsJson`, `SkillLevel`, `MaxOpenTickets`, `Weight`, `AcceptsAiAssignment`). O comentário da linha 3 é explícito: *"usado pelo round-robin de atribuição"*. **Não há papel/permissão no vínculo** (nenhum `IsManager`, `CanClose`, `Role`). |

### 2.2 DepartmentTeamResolver / IDepartmentTeamResolver

`src/Discovery.Infrastructure/Services/DepartmentTeamResolver.cs`

- `ResolveMembersAsync` (linhas 25-49): equipe efetiva = membros ativos do próprio
  departamento (linhas 38-39) **+ membros ativos herdados do departamento global pai**
  (linhas 41-46), deduplicados por `UserId`.
- `LoadMembersAsync` (linhas 101-133): filtra `m.IsActive` **e** `u.IsActive`
  (linhas 104-105); ordena por `CreatedAt, UserId`.
- Retorna `DepartmentTeamMember` (`src/Discovery.Core/Interfaces/IDepartmentTeamResolver.cs:8-17`),
  que carrega **apenas perfil de triagem** — nenhum campo de autorização.

O XML-doc da interface é inequívoco (`IDepartmentTeamResolver.cs:19-26`): *"Ponto único de
resolução da equipe **candidata**... usado pela auto-atribuição determinística e pela
triagem por IA."* **"Candidata" é o termo-chave: candidata a receber atribuição, não a
autorizar tratamento.**

### 2.3 Para que o vínculo é realmente usado

Todos os consumidores de `DepartmentMember`/`IDepartmentTeamResolver`:

| Consumidor | Localização | Uso |
| --- | --- | --- |
| `TicketAssignmentService.ResolveFallbackAsync` | `src/Discovery.Infrastructure/Services/TicketAssignmentService.cs:70-107` | Round-robin (linhas 81-95) e LeastOpenTickets (linhas 97-106). **Escolhe a quem atribuir.** |
| `AiTicketTriageService` | `src/Discovery.Infrastructure/Services/AiTicketTriageService.cs:380` | Lista de candidatos para a IA escolher. |
| `AiAssignmentHandlers` | `src/Discovery.Infrastructure/Cqrs/Support/AiAssignmentHandlers.cs:71, 96, 179, 187` | Preview/apply da triagem. |
| `AiAssignmentLearningService` | `src/Discovery.Infrastructure/Services/AiAssignmentLearningService.cs:121, 415, 433` | Afinidade/aprendizado. |
| `TechnicianMetricsService` | `src/Discovery.Infrastructure/Services/TechnicianMetricsService.cs:441, 557` | Métricas. |
| Handlers de CRUD do vínculo | `src/Discovery.Infrastructure/Cqrs/Support/SupportProductivityHandlers.cs:464-551` | Adicionar/remover/listar membro. |

**Nenhum desses caminhos é uma decisão de autorização.** São todos caminhos de
*"para quem mandar o chamado"*, nunca *"quem pode mexer no chamado"*.

### 2.4 O vínculo não confere nem a capacidade de tratar

Detalhe importante: como `AssignedToUserId` é aceito sem validação de time, um chamado
pode ser atribuído a alguém que **não** é membro do departamento:

- `TicketsController.Update` (PUT) aceita `AssignedToUserId` do corpo e só passa por
  `CanAccessTicketAsync` por Cliente/Site
  (`src/Discovery.Api/Controllers/TicketsController.cs:113-122`).
- `TicketCommandService.UpdateTicketAsync` aplica `ticket.AssignedToUserId = assignedToUserId`
  sem verificar vínculo com o departamento
  (`src/Discovery.Infrastructure/Services/TicketCommandService.cs:200-216`).

Ou seja, nem o "quem atende" nem o "quem pode atender" são consistentes hoje.

---

## 3. Gap analysis — veredito por regra do requisito

| # | Regra do requisito | Veredito | Evidência |
| --- | --- | --- | --- |
| R1 | Cada departamento tem seu TIME de atendimento | **ATENDE PARCIAL** | O vínculo existe e é resolvido com herança do global (`DepartmentTeamResolver.cs:25-49`), mas o time é modelado como **fila de atribuição**, não como time de autorização — `DepartmentMember.cs:3`; `IDepartmentTeamResolver.cs:19-26`. Não há papel dentro do vínculo (`DepartmentMember.cs:4-28`). |
| R2 | SOMENTE membros do time do departamento podem TRATAR/ATENDER chamados daquele departamento | **NÃO ATENDE** | A autorização usa apenas Cliente/Site: `TicketAccessGuard.cs:29-30`; `TicketsController.cs:59-60`. `ScopeLevel` não tem `Department` (`ScopeLevel.cs:9-14`). Busca por `CanTreatTicket`/`IsDepartmentMember`/`IsMemberOf` = **0 ocorrências**. |
| R3 | Demais usuários PODEM ABRIR chamado para o departamento | **ATENDE** | `TicketsController.Create` valida escopo por Cliente/Site e grava `RequesterUserId = CurrentUserId` (`TicketsController.cs:84-101`, em especial linha 100); `TicketCommandService.cs:120`. Não há restrição de departamento na abertura — que é exatamente o comportamento desejado. |
| R4 | Demais usuários só podem FECHAR o chamado que ELE MESMO abriu | **NÃO ATENDE** | Não existe verificação de autoria. Comparações `RequesterUserId ==` = **0 ocorrências**. `UpdateWorkflowState` exige apenas `Tickets.Edit` no Cliente/Site (`TicketsController.cs:124-150`). Qualquer usuário com `Tickets.Edit` fecha qualquer chamado — e hoje os roles `Operator` e `Support` têm `Tickets.Edit` (`src/Discovery.Migrations/Migrations/M123_SeedRolePermissions.cs:26, 38`). |
| R5 | Agentes remotos: fechar somente chamados que ele mesmo abriu | **ATENDE PARCIAL** | Existe posse, mas **por `AgentId`, não por autoria**: `CloseAndRateMyTicketHandler` exige `ticket.AgentId == cmd.AgentId` (`src/Discovery.Infrastructure/Cqrs/AgentAuth/Handlers/AgentTicketHandlers.cs:442-444`). O mesmo padrão em reopen (`:403-407`) e rating (`:425-427`). Ver ressalva em 3.1. |
| R6 | Time de atendimento atende TODOS os chamados do departamento ao qual tem vínculo | **NÃO ATENDE** | O time não concede nenhum acesso. Hoje o acesso vem de Cliente/Site, então o time atende **todos os chamados do cliente/site**, inclusive de departamentos aos quais não tem vínculo — o inverso do requisito. `TicketQueryService.cs:61-63`. |

### 3.1 Ressalva sobre R5 (importante)

O requisito diz "fechar somente o chamado que ele mesmo **abriu**". O código garante
"fechar somente o chamado **vinculado à própria máquina**" (`ticket.AgentId`).

Isso é mais permissivo do que o requisito em um cenário real: vários chamados podem
apontar para o mesmo `AgentId` — inclusive chamados **criados automaticamente por
alerta/sensor**, que têm `RequesterUserId = null`
(`src/Discovery.Infrastructure/Services/AlertToTicketService.cs:118-139`; o objeto
`Ticket` criado ali **não** define `RequesterUserId`). Um agent autenticado naquela
máquina fecharia chamados que a máquina não abriu por ação de usuário. Portanto
**ATENDE PARCIAL**, não ATENDE.

---

## 4. Superfícies de escrita de chamado que precisam de guarda

Todas abaixo exigem hoje apenas `Tickets.*` no escopo Cliente/Site. **Nenhuma** valida
departamento/time nem autoria.

### 4.1 TicketsController (a superfície principal)

| Endpoint | Linha | Permissão atual | Guarda de escopo hoje |
| --- | --- | --- | --- |
| `PUT /tickets/{id}` (Update) | `TicketsController.cs:113-122` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` — só Cliente/Site |
| `PATCH /tickets/{id}/workflow-state` | `TicketsController.cs:124-150` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` — **é o caminho de FECHAR** |
| `DELETE /tickets/{id}` (Delete) | `TicketsController.cs:156-166` | `Tickets.Delete` | `CanAccessTicketAsync(id, Delete)` |
| `POST /tickets/{id}/comments` (AddComment) | `TicketsController.cs:185-195` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |
| `POST /tickets/{id}/merge` (MergeTickets) | `TicketsController.cs:220-239` | `Tickets.Edit` | `CanAccessTicketAsync` no alvo (224) **e** em cada origem (231) |
| `POST /tickets/{id}/runbook/execute` | `TicketsController.cs:202-218` | `Tickets.Execute` | `CanAccessTicketAsync(id, Execute)` — **executa script na máquina** |
| `POST /tickets/{id}/reopen` | `TicketsController.cs:428-437` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |
| `POST /tickets/{id}/rating` | `TicketsController.cs:439-449` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |
| `POST /tickets/{id}/watchers` | `TicketsController.cs:284-292` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |
| `POST /tickets/{id}/relations` | `TicketsController.cs:400-413` | `Tickets.Edit` | alvo + `TargetTicketId` (406) |
| `PUT /tickets/{id}/custom-fields/{definitionId}` | `TicketsController.cs:918-932` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |
| `POST /tickets/{id}/attachments/*` | `TicketsController.cs:537-630` | `Tickets.Edit` | `CanAccessTicketAsync` (541 usa View; 580 usa Edit) |
| `POST /tickets/{id}/knowledge-links` etc. | `TicketsController.cs:316-386` | `Tickets.Edit` | `CanAccessTicketAsync(id, Edit)` |

Observações de inconsistência já existentes (relevantes ao endurecimento):

- `PrepareAttachmentUpload` (`:537-571`) e `PrepareCommentAttachmentUpload` (`:657-691`)
  checam **`View`** embora declarem `[RequirePermission(..., ActionType.Edit)]`.
- `MergeTickets` valida as origens com `ActionType.View` (`:231`) mas o comando
  **modifica** as origens.

### 4.2 Superfície SEM nenhuma guarda de escopo de chamado (grave)

| Endpoint | Linha | Permissão | Problema |
| --- | --- | --- | --- |
| `POST /tickets/{id}/ai/triage` | `TicketAiController.cs:15-21` | `Tickets.View` | **Não chama `CanAccessTicketAsync`.** Vaza conteúdo do chamado. |
| `POST /tickets/{id}/ai/summarize` | `TicketAiController.cs:24-30` | `Tickets.View` | idem |
| `POST /tickets/{id}/ai/suggest-reply` | `TicketAiController.cs:33-39` | `Tickets.View` | idem |
| `POST /tickets/{id}/ai/draft-kb-article` | `TicketAiController.cs:42-48` | `Tickets.View` | idem — com `persist=true` **grava** artigo na KB |

O controller `TicketAiController` inteiro (`TicketAiController.cs:10-49`) não injeta
`IScopeContext` nem `ITicketAccessGuard` e não tem uma única checagem por chamado.
Qualquer usuário com `Tickets.View` global lê o conteúdo de qualquer chamado por aqui.

### 4.3 TicketAssignmentController

| Endpoint | Linha | Permissão | Guarda |
| --- | --- | --- | --- |
| `GET /tickets/assignment/{ticketId}/decision` | `TicketAssignmentController.cs:25-29` | `Tickets.View` | **Nenhuma** — expõe candidatos e decisão da IA |
| `POST /tickets/assignment/{ticketId}/preview` | `TicketAssignmentController.cs:32-37` | `Tickets.Edit` | **Nenhuma** |
| `POST /tickets/assignment/{ticketId}/apply` | `TicketAssignmentController.cs:40-45` | `Tickets.Edit` | **Nenhuma** — **atribui o chamado** a um membro do time, sem checar o departamento do ator |
| `GET /tickets/assignment/decisions` | `TicketAssignmentController.cs:48-61` | `Tickets.View` | **Nenhuma** — auditoria cross-tenant |

Este é o candidato mais direto para "AssignTicket" citado no enunciado.

### 4.4 TicketChatController (394 linhas) — guarda por `ITicketAccessGuard`

Todas as rotas usam `accessGuard.CanAccessTicketAsync` (mesma regra Cliente/Site):
`GetCurrentSession` (`:47-56`), `GetHistory` (`:59-68`), `GetMessages` (`:71-84`),
`TouchPresence` (`:94-106`), `StartSession` (`:122-140`), `RetryInvite` (`:143-165`),
`SendMessage` (`:168-192`), `EndSession` (`:195-208`), anexos (`:213-350`).
**Não há checagem de departamento.** Um usuário fora do time abre uma sessão de chat
com o usuário final do chamado.

### 4.5 SupportChatController — guarda por agente/ACL, não por chamado

Rotas `/support-chat/agents/{agentId}/sessions` e `/support-chat/sessions/{id}/...`
(`SupportChatController.cs:75-318`). A guarda é `CanAccessAgentScopeAsync`
(`SupportChatController.cs:97-99`) — escopo da **máquina**, não do chamado. Sessões
avulsas (sem chamado) escapam por completo do conceito de departamento do chamado.

### 4.6 AutoTicketRulesController

`AutoTicketRulesController.cs`: `POST` `:34-40`, `PUT` `:42-48`, `DELETE` `:50-56`,
`PATCH enable/disable` `:60-66`, `POST simulate` `:88-94`, `POST {id}/dry-run` `:97-103`,
`POST seed-defaults` `:118-124`; e `AutoTicketPolicyController` `PUT` `:150-174`.
Tudo com `Tickets.Edit`/`Tickets.View` **global e sem escopo por cliente/site**
(repare que o padrão `ScopeSource.AccessList` não é usado nessas rotas). Um usuário
com `Tickets.Edit` global pode reconfigurar a criação automática de chamados de
**qualquer cliente**.

### 4.7 TicketTemplatesController

`TicketTemplatesController.cs`: `POST` `:28-32`, `PUT {id}` `:33-42`,
`DELETE {id}` `:43-53`, `POST {id}/restore` `:54-58` — todos `Tickets.Edit` **global**.
Templates determinam os campos/questionário do departamento, logo são parte da
superfície de configuração do atendimento.

### 4.8 Outras superfícies correlatas

| Controller | Escritas | Permissão |
| --- | --- | --- |
| `TicketMacrosController.cs` | `:21, 26, 31` | `Tickets.*` (não verificado o escopo exato linha a linha) |
| `TicketAlertRulesController.cs` | `:40, 48, 56, 64` | `Tickets.*` (não verificado o escopo exato) |
| `EscalationRulesController.cs` | `:44, 52, 60` | `Tickets.*` (não verificado o escopo exato) |
| `AgentAuthController` (agent) | `:947-958` close, `:964-974` reopen, `:979-992` rating, `:392-400` create, `:402-411` comment | Identidade do agent (`mdz_` token); posse por `AgentId` |

---

## 5. Vazamento de leitura: como a listagem filtra e o que um usuário fora do time vê

### 5.1 Caminho da listagem

1. `TicketsController.GetAll` (`TicketsController.cs:65-71`) — `[RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]`.
   O filtro `AccessList` (`AuthFilters.cs:273-290`) só exige **algum** escopo válido — a
   filtragem fina fica para a query.
2. `ListTicketsQueryHandler.Handle` (`src/Discovery.Infrastructure/Cqrs/Tickets/QueryHandlers/TicketQueryHandlers.cs:15-28`)
   injeta o ACL no filtro:
   ```csharp
   var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
   var filter = q.Filter with {
       HasGlobalAccess = access.HasGlobalAccess,
       AllowedClientIds = access.AllowedClientIds,
       AllowedSiteIds = access.AllowedSiteIds
   };
   ```
3. `TicketQueryService.ListTicketsAsync` (`TicketQueryService.cs:27-91`) aplica a
   row-level security (linhas 50-64):
   ```csharp
   if (!f.HasGlobalAccess) {
       var allowedClientIds = (f.AllowedClientIds ?? []).Distinct().ToArray();
       var allowedSiteIds = (f.AllowedSiteIds ?? []).Distinct().ToArray();
       if (allowedClientIds.Length == 0 && allowedSiteIds.Length == 0) { /* devolve vazio */ }
       query = query.Where(t =>
           allowedClientIds.Contains(t.ClientId) ||
           (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
   }
   ```

### 5.2 O que um usuário fora do time vê hoje

**Todos os chamados do cliente/site**, de todos os departamentos. `t.DepartmentId` é
apenas **item de filtro opcional** vindo da query string
(`TicketFilterQuery.DepartmentId`, `src/Discovery.Core/DTOs/TicketDtos.cs:17`;
aplicado em `TicketQueryService.cs:36`) — **nunca uma restrição imposta pelo servidor**.
O agrupamento por departamento é apenas visual: `DepartmentId` é projetado no
`TicketListItemDto` (`TicketQueryService.cs:81`) mas não entra na cláusula de segurança.

O mesmo vale para o detalhe: `GetById` (`TicketsController.cs:73-82`) usa
`CanAccessTicketAsync` → Cliente/Site.

**Agravante — notas internas.** `GetComments` calcula
`includeInternal = CanAccessTicketAsync(id, ActionType.Edit)`
(`TicketsController.cs:179`). Como `Edit` é concedido por Cliente/Site, um usuário fora
do time — mas com `Tickets.Edit` no cliente — **lê as notas internas do time**.

**Agravante — listagens sem ACL.** `SearchAnswers` (`TicketsController.cs:941-957`) usa
`ScopeSource.AccessList` mas o handler de busca **não recebe ACL** (não foi injetado
`IScopeContext` no caminho verificado). O mesmo padrão aparece em `GetCsatSummary`
(`:973-980`) e `GetKpi` (`:984-990`), que aceitam `DepartmentId`/filtro livre.
**Classificação: potencialmente vazante — não verificado até o handler**; recomendo
auditoria dedicada.

**Agravante — `TicketAiController`**: já descrito em 4.2; sem `CanAccessTicketAsync`,
qualquer `Tickets.View` global lê o conteúdo por `/ai/summarize` conhecendo o GUID.

---

## 6. Riscos e efeitos colaterais de introduzir autorização por departamento

| # | Risco | Detalhe e evidência |
| --- | --- | --- |
| 1 | **Cache Redis `perm:user:` não conhece departamento** | `CachePrefix = "perm:user:"`, TTL 300s (`PermissionService.cs:20-21`). O payload cacheado é uma lista achatada de `(RoleId, ScopeLevel, ScopeId, ResourceType, ActionType)` (`PermissionService.cs:147-168`) e a releitura agrupa por essas mesmas chaves (linhas 118-137). **Não há onde acomodar vínculo de departamento** — qualquer nova dimensão exige mudar o formato do cache e **invalidar todas as chaves existentes**, ou o vínculo ficaria "invisível" por até 5 min após alteração de time. |
| 2 | **Invalidação de cache incompleta** | `AddDepartmentMember`/`RemoveDepartmentMember` (`SupportProductivityHandlers.cs:440-484, 536-540`) **não chamam** `InvalidateUserCacheAsync`. Hoje isso é inócuo (o vínculo não autoriza nada); ao passar a autorizar, **remover alguém do time não revogaria o acesso por até 5 minutos**. |
| 3 | **Performance / N+1** | `CanAccessTicketAsync` já faz 1 query extra por chamado (`TicketAccessGuard.cs:25`). Adicionar checagem de time significa mais 1-2 queries por request (resolver a equipe do departamento — `DepartmentTeamResolver.cs:28-46` faz **até 3 queries**). Em listagens, a decisão precisa ser feita **em conjunto** (join/SQL), não por item, ou o custo explode. O ponto de extensão natural é `TicketQueryService.cs:61-63`. |
| 4 | **Chamados SEM departamento** | `Ticket.DepartmentId` é **nullable** (`src/Discovery.Core/Entities/Ticket.cs:18`). Nada garante departamento no create: `TicketsController.Create` (`:84-111`) não exige `DepartmentId`. Qualquer política "só o time do departamento atende" precisa de uma regra explícita e testada para `DepartmentId == null` (fail-open ou fail-closed), senão ou tranca chamados legítimos ou abre um buraco. |
| 5 | **Departamentos globais e herança de um salto** | `Department.ClientId == null` = global (`Department.cs:13-15`); um departamento de cliente herda membros do global pai via `InheritFromGlobalId` (`Department.cs:20-24`; `DepartmentTeamResolver.cs:41-46` com `ResolveGlobalParentAsync` em `:55-99`). Se autorização usar o mesmo resolvedor, **um departamento global vira "time de tudo"** — provavelmente indesejado. Além disso, mexer no time do global passa a alterar permissões de **todos** os departamentos de clientes que herdam dele, silenciosamente. |
| 6 | **Chamados criados sem usuário (auto-ticket / sensor / alerta)** | `AlertToTicketService.CreateTicketFromMonitoringEventAsync` (`AlertToTicketService.cs:89-166`) cria o `Ticket` (linhas 118-136) **sem `RequesterUserId`** — logo a regra "fechar só o que eu abri" é **insatisfazível** para esses chamados; eles precisam cair obrigatoriamente no time do departamento, ou ficam órfãos/intratáveis. O mesmo vale para chamados sem departamento (risco 4). |
| 7 | **Agente remoto não tem identidade de usuário** | `CloseAndRateMyTicketHandler` valida posse por `ticket.AgentId == cmd.AgentId` (`AgentTicketHandlers.cs:443`), não por `RequesterUserId`. Introduzir "fechar só o que abriu" para agentes exige decidir a semântica: por `AgentId` (atual, mais permissivo) ou por solicitante real. Ver ressalva 3.1. |
| 8 | **Bypass pela porta dos fundos** | Endurecer `CanAccessTicketAsync` **não** cobre os caminhos que não o chamam: `TicketAiController` (4.2) e `TicketAssignmentController` (4.3). Um `apply` de triagem atribui o chamado a um membro do time sem checar o departamento do ator; um `summarize` lê o chamado inteiro. **A guarda tem de ser central, não por endpoint.** |
| 9 | **Configuração de triagem confundida com autorização** | `AssignmentStrategyOverride`, `AiAssignment*Override` e a herança do global (`Department.cs:29-90`; `TicketAssignmentService.cs:46-64`) hoje governam **atribuição**. Se a autorização reusar esse resolvedor sem separar os conceitos, mudar uma preferência de triagem passaria a **conceder ou revogar acesso**, o que é um acoplamento perigoso. |
| 10 | **Regressão em testes e no portal** | Os testes existentes cobrem o vínculo apenas pelo ângulo de atribuição/métricas (ex.: `src/Discovery.Tests/DepartmentTeamResolverTests.cs`, `DepartmentAssignmentTeamMetricsTests.cs`, `TechnicianMetricsServiceTests.cs:351-377`). **Não localizei nenhum teste de autorização por departamento** — a suíte atual passaria mesmo com a regra ausente, então ela não serve de rede de segurança para esta mudança. |
| 11 | **Roles atuais já têm `Tickets.Edit` amplo** | `Operator` (`M123_SeedRolePermissions.cs:26`) e `Support` (`:38`) recebem `Tickets.Edit`. Introduzir restrição por departamento **quebra o fluxo atual** desses perfis até que os vínculos de time sejam populados — exige plano de migração/backfill. |
| 12 | **Validação de transferência de departamento é só de tenant** | `EnsureDepartmentBelongsToClientAsync` (`TicketCommandService.cs:414-419`) valida existência, `IsActive` e cliente — **nada de time**. Um usuário fora do time pode **mover** um chamado para outro departamento (`TicketsController.cs:113-122` → `TicketCommandService.cs:263-279`), contornando qualquer regra por departamento que venha a existir. |

---

## 7. Conclusão

O requisito **não é atendido** pelo backend na forma atual. Os dois eixos que o requisito
exige — (a) autorização por departamento/time e (b) autorização por autoria do chamado —
**não existem** no código: a hierarquia de escopo termina em Site
(`ScopeLevel.cs:9-14`) e nenhuma comparação de solicitante é feita (0 ocorrências de
`RequesterUserId ==`).

O que já existe e pode ser reaproveitado como **fundação**:

- `DepartmentMember` + `IDepartmentTeamResolver` já resolvem "quem é o time" com herança
  e ordenação estáveis (`DepartmentTeamResolver.cs:25-49`) — falta dar a esse vínculo
  semântica de permissão.
- `Ticket.RequesterUserId` já é gravado na abertura pela console
  (`TicketsController.cs:100`; `TicketCommandService.cs:120`) — falta usá-lo para autorizar.
- `TicketAccessGuard`/`CanAccessTicketAsync` já é um ponto de decisão único e é o lugar
  natural para centralizar a nova regra (hoje duplicado em
  `TicketAccessGuard.cs:16-31` e `TicketsController.cs:48-61` — **unificar antes de estender**).

Correções que **não** podem ficar de fora de qualquer plano:

1. Adicionar um nível de escopo/departamento ou um resolvedor de time dedicado à
   autorização, **separado** da triagem por IA.
2. Estender o payload do cache `perm:user:` e invalidá-lo em add/remove de membro.
3. Centralizar a guarda e cobrir os caminhos que hoje **não** a chamam
   (`TicketAiController`, `TicketAssignmentController`).
4. Definir e testar explicitamente a política para `DepartmentId == null`, departamentos
   globais, departamentos inativos e chamados automatizados sem `RequesterUserId`.

---

## Anexo A — Arquivos que precisariam mudar

Classificados por papel na mudança. **Nenhum foi alterado.**

### Núcleo de autorização

| Arquivo | Motivo |
| --- | --- |
| `src/Discovery.Core/Enums/Identity/ScopeLevel.cs` | Avaliar nível de escopo por departamento (ou modelo alternativo). |
| `src/Discovery.Core/Enums/Identity/ActionType.cs` | Avaliar ação específica de fechamento/tratamento. |
| `src/Discovery.Core/Interfaces/Auth/IPermissionService.cs` | `UserScopeAccess` precisa carregar departamentos permitidos. |
| `src/Discovery.Infrastructure/Services/PermissionService.cs` | `IsScopeMatch` (`:170-195`), `GetScopeAccessAsync` (`:51-84`), formato e invalidação do cache (`:108-221`). |
| `src/Discovery.Infrastructure/Services/ScopeContext.cs` | Cache e exposição do departamento resolvido (`:32-35`, `:50-62`). |
| `src/Discovery.Api/Filters/AuthFilters.cs` | `ResolveScopeFromRoute` (`:327-364`) não conhece `departmentId`. |

### Guarda de chamado (unificar e estender)

| Arquivo | Motivo |
| --- | --- |
| `src/Discovery.Infrastructure/Services/TicketAccessGuard.cs` | Implementação principal da regra (`:16-31`). |
| `src/Discovery.Core/Interfaces/ITicketAccessGuard.cs` | Contrato da guarda. |
| `src/Discovery.Api/Controllers/TicketsController.cs` | `CanAccessTicketAsync` duplicado (`:48-61`) + 13+ pontos de escrita (seção 4.1). |
| `src/Discovery.Api/Controllers/TicketAiController.cs` | **Sem qualquer guarda** (`:10-49`). |
| `src/Discovery.Api/Controllers/TicketAssignmentController.cs` | **Sem qualquer guarda** (`:25-61`). |
| `src/Discovery.Api/Controllers/TicketChatController.cs` | Guarda só Cliente/Site em todas as rotas. |
| `src/Discovery.Api/Controllers/SupportChatController.cs` | Guarda por máquina, não por chamado (`:97-99`). |
| `src/Discovery.Api/Controllers/AutoTicketRulesController.cs` | Configuração global de tickets sem escopo. |
| `src/Discovery.Api/Controllers/TicketTemplatesController.cs` | Templates globais (`:28-58`). |

### Serviços de domínio

| Arquivo | Motivo |
| --- | --- |
| `src/Discovery.Infrastructure/Services/TicketWorkflowService.cs` | `TransitionAsync` (`:44-75`) não conhece departamento — é onde o "fechar" acontece. |
| `src/Discovery.Infrastructure/Services/TicketCommandService.cs` | Create (`:108-170`), Update (`:172-279`), transferência (`:414-419`). |
| `src/Discovery.Infrastructure/Services/TicketQueryService.cs` | Row-level security da listagem (`:50-64`) — ponto de extensão para SQL em conjunto. |
| `src/Discovery.Infrastructure/Cqrs/Tickets/QueryHandlers/TicketQueryHandlers.cs` | Injeção do ACL (`:15-28`). |
| `src/Discovery.Infrastructure/Services/DepartmentTeamResolver.cs` | Resolvedor da equipe (`:25-49`) — reuso ou separação. |
| `src/Discovery.Core/Interfaces/IDepartmentTeamResolver.cs` | Contrato da equipe (`:27-36`). |
| `src/Discovery.Infrastructure/Cqrs/Support/SupportProductivityHandlers.cs` | Add/Remove membro (`:440-540`) — precisa invalidar cache. |
| `src/Discovery.Infrastructure/Cqrs/AgentAuth/Handlers/AgentTicketHandlers.cs` | Regra de posse do agent (`:403-407`, `:425-427`, `:442-444`). |
| `src/Discovery.Infrastructure/Services/AlertToTicketService.cs` | Chamados automatizados sem `RequesterUserId` (`:118-139`). |

### Dados e migração

| Arquivo | Motivo |
| --- | --- |
| `src/Discovery.Core/Entities/DepartmentMember.cs` | Avaliar papel/permissão no vínculo (`:4-28`). |
| `src/Discovery.Core/Entities/Ticket.cs` | `DepartmentId` nullable (`:18`) — política para nulo. |
| `src/Discovery.Migrations/Migrations/` (nova migration) | Backfill/validação de vínculos + eventual seed de permissão. |
| `docs/PERMISSIONS_MATRIX.md` | Documentar o novo eixo (`:164-196` hoje só cobre Cliente/Site). |

---

## Anexo B — Itens não verificados

Registrados para honestidade metodológica; **não** devem ser tratados como achados:

1. **Escopo exato de permissão** de `TicketMacrosController`, `TicketAlertRulesController`
   e `EscalationRulesController` — não li as anotações `[RequirePermission]` linha a linha.
2. **Handlers de `SearchAnswers`, `GetCsatSummary` e `GetKpi`** — se aplicam ACL por
   cliente/site internamente não foi verificado; o risco apontado na seção 5.2 é
   **potencial**, não confirmado.
3. **Comportamento do portal/console (React)** — fora do escopo desta análise de backend.
4. **Cobertura de testes existente** sobre autorização por departamento: a ausência foi
   inferida por ausência de símbolos de autorização no código de produção; não enumerei
   a suíte de testes inteira.
5. **Índices de banco** para o eventual `join` de departamento em listagens — não avaliado.
