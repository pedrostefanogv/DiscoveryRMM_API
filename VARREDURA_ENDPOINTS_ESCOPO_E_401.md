# Varredura do backend — escopo Global implícito e 401 silencioso

Continuação de `AUDITORIA_ROLES_PERMISSOES_PEDROSTEFANOGV.md`. Aqui o alvo não é um usuário,
e sim **onde mais a mesma classe de problema reaparece** no `DiscoveryRMM_API`.

Escopo: apenas leitura de código. Nenhum arquivo-fonte foi alterado.

**Números apurados (grep sobre `src/`, 55 controllers):**

| Categoria | Qtd |
|---|---|
| Atributos `[RequirePermission]` no total | **561** |
| Sem `ScopeSource` ⇒ **`Global` implícito** | **479 (85%)** |
| Com `ScopeSource.AccessList` | 58 |
| Com `ScopeSource.FromRoute` | 24 |

> O default é `Global` porque o construtor de 2 argumentos **fixa** o valor:
> `AuthFilters.cs:194-199` — `_scopeSource = ScopeSource.Global;`. Não há override por
> convenção, configuração ou atributo de classe: quem não escreve o 3º argumento recebe
> Global, sempre.

---

## Resumo executivo

1. **Classe A (401 silencioso) é real, e o gatilho é a AUSÊNCIA do header `Authorization`.**
   O front autentica **exclusivamente por Bearer token em memória** — não existe cookie de
   sessão no sistema (grep por `CookieAuthentication|Request.Cookies|AppendCookie` = **0
   ocorrências**). Consequência: qualquer navegação que o navegador faça sozinho
   (`<a href download>`, `<video><source src>`, `<img src>`, `window.open` para URL da API)
   **não transporta credencial alguma**. Não é uma hipótese: o próprio repositório já
   documentou isso em dois pontos (ver §1.3).
2. **O download de gravação de terminal (`RemoteSessionsController.cs:257-280`) é o caso
   mais grave:** o front monta a URL e o `<a href>`/player a abrem por navegação direta, mas
   o endpoint exige `[RequirePermission]` e não é público. O usuário recebe **401 cru**
   (o filtro não loga) ou um `<video>` mudo com "Falha ao carregar o vídeo".
3. **Existe uma inconsistência de escopo dentro da MESMA família de endpoints.** Em
   `RemoteSessionsController`, 16 ações usam `Global` e 3 usam `AccessList`, sendo que
   `recording/download` (Global) e `recording/terminal/download` (AccessList) fazem
   praticamente a mesma coisa. Um admin rebaixado a escopo Client/Site perde uma e mantém
   a outra — o sintoma fica intermitente e parece aleatório.
4. **Seis controllers misturam as duas semânticas** (tabela em §2.3),
   e em `SitesController` a omissão recai justamente sobre as operações de **energia em
   massa** (:191, :202, :213) — as mais destrutivas do controller.

---

## 1. Classe A — 401 silencioso sem log

### 1.1 Onde o 401 nasce

`src/Discovery.Api/Filters/AuthFilters.cs:249-255`:

```csharp
public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
{
    if (context.HttpContext.Items["UserId"] is not Guid userId)
    {
        context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
        return;
    }
```

Três características que tornam este ponto difícil de diagnosticar:

- **Não há `_logger` no filtro.** A classe `RequirePermissionFilter` (`:218-247`) só recebe
  `IPermissionService`, `IScopeContext`, `ResourceType`, `ActionType`, `ScopeSource`. Não há
  nenhum log no caminho.
- **A mensagem não identifica o endpoint** ("Autenticação necessária."). Sem o path, um HAR
  com vários 401 não diz qual filtro disparou.
- **O valor de `Items["UserId"]` não é observável** sem instrumentação.

O único lugar que popula `Items["UserId"]` com JWT de usuário é
`src/Discovery.Api/Middleware/UserAuthMiddleware.cs:82`. Há também
`ApiTokenAuthMiddleware.cs:65` (tokens de API, outro fluxo).

### 1.2 Por que o header some: o front não usa cookie

`src/api/client.ts:380-385` (transporte padrão):

```typescript
if (useAuth && !headers.has("Authorization")) {
  const accessToken = apiClientConfig.getAccessToken();
  if (accessToken) {
    headers.set("Authorization", `Bearer ${accessToken}`);
  }
}
```

O token vem de uma **closure JavaScript** (`getAccessToken`), configurada em
`AuthContext.tsx:333` e, na popup de acesso remoto, em `RemoteSession.tsx:229`. Não existe
`credentials: "include"` em nenhum `fetch` do client, e não há cookie de autenticação no
backend. Portanto:

> **Resposta direta à pergunta do lead:** não, o `<a href>` **não** carrega o header de
> jeito nenhum. Não é uma limitação contornável por atributo (`download`, `target`,
> `rel`): headers são definidos por quem faz a requisição, e numa navegação o navegador é
> quem faz. Só existem duas saídas — (a) o endpoint ser público/semi-público por design
> (token na URL ou `[AllowAnonymous]`), ou (b) o front buscar via `apiFetch` e entregar um
> `Blob` (object URL). Enquanto for `href` para um endpoint `[RequirePermission]`, o 401 é
> inevitável.

Único caminho alternativo que o backend aceita é **query string**, e só para o hub NATS
(`UserAuthMiddleware.cs:41, 46-63`). Isto é, o codebase já tem o padrão "token na query
string" implementado — mas restrito a `/nats`.

### 1.3 O repositório já documentou esse bug (evidência forte)

Não estou inferindo a classe do problema; ela está escrita no código.

`src/Discovery.Api/Controllers/TicketsController.cs:557-565`:

```csharp
// Streaming same-origin: um <a href> não envia Authorization e o
// redirect para o storage exigiria CORS. Aqui a API autentica e serve.
var stream = await attachmentService.DownloadAttachmentAsync(attachmentId, ct);
...
return File(stream, contentType, attachment.FileName);
```

`src/Discovery.Api/Controllers/AppStoreController.cs:810-816`:

```csharp
/// Serve o icone/capa de um pacote custom. Anonimo de proposito: e usado
/// diretamente em tag &lt;img&gt; pela loja, que nao envia o header Authorization.
/// Somente imagens sao servidas.
[HttpGet("custom/{packageId}/icon")]
[AllowAnonymous]
```

Ou seja: **onde o time lembrou, o endpoint foi corrigido** — e o padrão correto se
consolidou no front (`downloadAttachmentBlob` em `api/tickets.ts:31`; `apiFetchResponse` +
Blob em `api/reports.ts:495`; Bearer em `api/knowledge.ts:37`). O risco, portanto, **não** é
o do `TicketsController`, que está resolvido nos dois lados. O risco está no endpoint
**novo que repetiu o padrão antigo sem a correção** — o player de gravação de terminal,
que monta URL crua e abre por `href`/`<source>` (§1.4 e §1.5).

### 1.4 Achado A-1 (CRÍTICO) — download da gravação de terminal

**Backend:** `src/Discovery.Api/Controllers/RemoteSessionsController.cs:257-280`

```csharp
[HttpGet("{agentId:guid}/recording/terminal/download")]
[RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
public async Task<IActionResult> DownloadTerminalRecording(...)
{
    ...
    if (!string.IsNullOrWhiteSpace(download.S3Url))
        return Redirect(download.S3Url);          // :276-277

    return File(download.Content!, "application/x-ndjson", download.FileName);   // :279
}
```

**Frontend:** o único consumidor é `RecordingPlayer.tsx`, que abre a URL por navegação
direta em **dois** pontos:

`src/modules/remote-recording/RecordingPlayer.tsx:30-36`:
```tsx
<a
  href={downloadUrl}
  download
  className="..."
>
  ⬇ Download
</a>
```

`src/modules/remote-recording/RecordingPlayer.tsx:44-49`:
```tsx
<video controls onError={() => setError('Falha ao carregar o vídeo')}>
  <source src={downloadUrl} type={mimeType} />
```

E a URL é montada crua, sem passar pelo client HTTP, em
`src/modules/remote-recording/useRecording.ts:26`:

```typescript
setDownloadUrl(`/api/v1/remote-sessions/${agentId}/recording/download`);
```

**Verificação de que `apiFetch` NÃO é usado aqui:** `useRecording.ts:26` é uma
template string atribuída a `useState`, não uma chamada a `api.get`. O
`api.get<RecordingDownload>` de `src/api/remote-sessions.ts:184-185` existe e autentica
corretamente, mas aponta para **outro** endpoint (`recording/download`, com `sessionId`) —
não para o `terminal/download` consumido pelo player.

**Sintoma que o usuário vê:**

| Cenário | O que aparece |
|---|---|
| Clicou "⬇ Download" | Aba/popup com **`{"message":"Autenticação necessária."}`** — JSON cru, sem tratamento. Nada é baixado. |
| Player tenta carregar o vídeo | Área preta + **"Falha ao carregar o vídeo"** (`RecordingPlayer.tsx:47`), porque o `onError` do `<video>` dispara no 401. |
| Log do servidor | **Nada.** O filtro não loga (classe A). |

**Gatilho:** sempre que o usuário clicar em download ou o player tentar tocar — **100% das
vezes**, não é intermitente. Se o storage é S3, nem se chega ao `Redirect`: o 401 acontece
antes de a action rodar (`OnActionExecutionAsync`), então a URL pré-assinada nunca é
emitida. Se o storage é Local, o `File(...)` de `:279` também nunca executa.

### 1.5 Achado A-2 — `recording/download` (o irmão próximo)

**Backend:** `RemoteSessionsController.cs:222-232`, com
`[RequirePermission(ResourceType.Agents, ActionType.View)]` em `:224` ⇒ **Global**.

**Frontend:** aqui há dois consumidores com sorte diferente:

- `src/api/remote-sessions.ts:184-185` usa `api.get` → **autentica** (Bearer anexado em
  `client.ts:380-385`). Funciona.
- `src/modules/remote-recording/useRecording.ts:26` monta a URL crua e o
  `RecordingPlayer` a abre via `href`/`<source>` → **não autentica**. Mesmo sintoma de A-1:
  JSON `Autenticação necessária.` ou vídeo mudo.

**Observação de risco:** `useRecording.ts:8` declara `{ agentId }` mas a interface
`UseRecordingParams` (`:3-6`) também exige `sessionId`, que **não é usado** na URL montada
em `:26`. A rota `{agentId:guid}/{sessionId:guid}/recording/download` exige `sessionId` —
logo, mesmo com autenticação resolvida, a URL montada em `:26` **não casa com a rota** e
resultaria em 404. São **dois** defeitos no mesmo caminho do player.

### 1.6 Demais endpoints que devolvem arquivo — varredura

Todos os `return File(...)/PhysicalFile(...)/Redirect(...)` do backend foram inspecionados:

| Endpoint | Atributo de auth | Navegável direto? | Risco |
|---|---|---|---|
| `DownloadController.cs:69, 91` | `[AllowAnonymous]` (`:16`) | Sim, por design (:13) | **OK** — instaladores |
| `AppStoreController.cs:835` | `[AllowAnonymous]` (`:816`) | Sim, por design (`:811-812`) | **OK** — ícone em `<img>` |
| `DeployTokensController.cs:105, 113` | Autenticado pelo próprio token na rota (`:82`) | Sim | **OK** — token no corpo, não header |
| `RemoteSessionsController.cs:277, 279` | `AccessList` (`:258`) | **Sim** (§1.4) | **CRÍTICO** |
| `RemoteSessionsController.cs` recording/download (`:224`) | `Global` | **Sim** (§1.5) | **ALTO** |
| `TicketsController.cs:565` | `Tickets:View` Global (`:542`) | **Não** — front usa Blob (`api/tickets.ts:34`) | **OK** |
| `TicketChatController.cs:336` | `Tickets:View` Global (`:315`) | **Não** — Blob (`api/tickets.ts:269-270`) | **OK** |
| `SupportChatController.cs:341` | `Tickets:View` Global (`:318`) | **Não** — Blob (`api/support-chat.ts:63`) | **OK** |
| `ReportsController.cs:96, 425` | `Reports:View/Execute` AccessList (`:85`) | **Não** — Blob (`api/reports.ts:495-521`) | **OK** |
| `KnowledgeController.cs:388` | `KnowledgeBase:View` Global (`:370`) | **Não** — Bearer (`api/knowledge.ts:37-45`) | **OK** |
| `AgentAuthController.cs:509, 786, 1115` | Fluxo de **agent** (token `mdz_`) | N/A | **OK** — não é usuário |

> **Correção importante (verificada depois da primeira redação):** eu havia marcado os
> anexos de ticket/chat, relatórios e imagens da KB como ALTO risco de 401. **Isso estava
> errado** — todos esses fronts usam `apiFetchResponse` + `Blob` + `URL.createObjectURL`, o
> que anexa o Bearer corretamente:
> - `api/tickets.ts:31-56` — helper `downloadAttachmentBlob`, com docstring em `:28-29`
>   dizendo explicitamente: *"um `<a href>` direto não envia o header e o endpoint antigo
>   redirecionava para o storage (bloqueado por CORS)"*.
> - `api/reports.ts:495-521` — `apiFetchResponse` → `blob` → `link.click()` com object URL.
> - `api/knowledge.ts:37-45` — "Baixa o binário da imagem pela API autenticada (Bearer, same-origin)".
>
> O padrão **correto já existe e está estabelecido** no codebase. Isso reforça o achado
> A-1: o problema não é falta de solução conhecida, e sim que **o caminho do
> `RecordingPlayer`/`useRecording` não segue o padrão** que o resto do front adota.

---

## 2. Classe B — escopo Global implícito

### 2.1 Semântica (recapitulando o que já foi provado)

Um `[RequirePermission(R, A)]` de 2 argumentos exige que o usuário tenha **atribuição de
escopo Global** (`AuthFilters.cs:198`). Com escopo Client/Site, `IsScopeMatch`
(`PermissionService.cs:170-195`) devolve `false` e a ação responde **403 "Permissão
insuficiente."** (`AuthFilters.cs:264-267`) mesmo que a permissão exista no catálogo.

**Sintoma:** 403, não 401. E o usuário *pode* ter a permissão — o que faz o suporte
suspeitar de bug. Este é o mesmo achado da auditoria anterior, agora generalizado.

### 2.2 Achado B-1 (ALTO) — inconsistência dentro de `RemoteSessionsController`

Mesma família, mesmo recurso (`Agents`), semânticas opostas:

| Linha | Ação | ScopeSource | Efeito para admin escopado (Client/Site) |
|---|---|---|---|
| `:70` | `StartSession` | **Global** | 403 |
| `:98` | `StopSession` | **Global** | 403 |
| `:111` | `RenewSession` | **Global** | 403 |
| `:128` | `GetActiveSessions` | **Global** | 403 |
| `:142` | `GetConsentStatus` | **Global** | 403 |
| `:177` | `GetSessionCredentials` | **Global** | 403 |
| `:198, :211` | `StartRecording`/`StopRecording` | **Global** | 403 |
| `:224` | `GetRecordingDownload` | **Global** | 403 |
| `:332, :353, :366, :415, :428, :441` | qualidade/banda/delete/tabs/shells | **Global** | 403 |
| `:238` | `GetTerminalRecording` | **AccessList** | **passa** |
| `:258` | `DownloadTerminalRecording` | **AccessList** | **passa** |
| `:284` | `DeleteTerminalRecording` | **AccessList** | **passa** |

**Inconsistência concreta:** `:224` (`recording/download`, Global) e `:258`
(`recording/terminal/download`, AccessList) são **ambos download de gravação** e diferem no
escopo. Pior: `:284` permite **excluir** a gravação de terminal com AccessList, enquanto
`:366` (`DeleteRecording`) **exige Global** — excluir é *menos* restritivo que acessar no
primeiro caso e *mais* no segundo.

**Sintoma:** o admin escopado vê a lista de gravações de terminal, consegue baixá-las e
excluí-las, mas **não consegue iniciar uma sessão de acesso remoto**. Parece
"permissão quebrada em alguns botões", e é justamente a assinatura descrita na task.

### 2.3 Achado B-2 (MÉDIO/ALTO) — 6 controllers misturam as duas semânticas

| Controller | Global | Explícito | Comentário |
|---|---|---|---|
| `AgentsController.cs` | 49 | 4 | 4 explícitos (`:76, :84, :97, :833`) num mar de Global |
| `TicketsController.cs` | 41 | 5 | idem |
| `SitesController.cs` | 4 | 13 | **os 4 Global são outliers** — ver B-3 |
| `RemoteSessionsController.cs` | 16 | 3 | ver B-1 |
| `AgentAlertsController.cs` | 7 | 1 | `:116` é o único AccessList |
| `DashboardController.cs` | 2 | 2 | |

**Sintoma:** comportamento aparentemente aleatório para o mesmo perfil de usuário — telas
que funcionam e telas que dão 403 no mesmo módulo.

### 2.4 Achado B-3 (ALTO) — `SitesController`: energia em massa é Global

Em `SitesController.cs`, 13 das 17 ações usam `FromRoute` (**correto**: resolvem
client/site da rota). As 4 exceções são Global:

| Linha | Ação | Risco |
|---|---|---|
| `:191` | `RestartSite` — reinicia **todos os agentes do site** | Alto |
| `:202` | `ShutdownSite` — desliga **todos os agentes do site** | Alto |
| `:213` | `WakeOnLanSite` | Médio |
| `:45` | `GetAll` (rota absoluta `/api/v1/sites`) | Baixo |

O contraste é nítido: `RestartSite`/`ShutdownSite` recebem `Guid clientId, Guid id` na
assinatura (`:192, :203`) — o escopo da rota **existe** e não é usado para autorizar.
Compare com `:81` (`Delete` de site), que usa `FromRoute`.

**Sintoma:** um administrador de cliente com `Agents:Execute` no escopo Client/Site
**não** consegue reiniciar/desligar os agentes do próprio site (403 "Permissão
insuficiente.") — enquanto consegue criar, editar e **excluir** o site (`:81`, FromRoute).
Há também um aspecto de segurança: as operações mais destrutivas do controller são as que
exigem o escopo **mais amplo**, o que é o inverso da intenção de menor privilégio.

### 2.5 Controllers 100% Global (sem nenhum escopo explícito)

Áreas inteiras que só funcionam para quem tem atribuição Global — relevantes porque são
tipicamente operadas por administradores:

| Controller | Nº de ações Global |
|---|---|
| `ConfigurationsController.cs` | 37 |
| `AppStoreController.cs` | 32 |
| `AgentLabelsController.cs` | 31 |
| `DepartmentsController.cs` | 23 |
| `KnowledgeController.cs` | 22 |
| `AutoTicketRulesController.cs` | 15 |
| `ClientsController.cs` | 13 |
| `RolesController.cs` | 9 |

**Contraste interno relevante:** `UsersController` e `UserGroupsController` usam
`AccessList` em **todas** as suas ações (21 no total), mas `RolesController` — que é a
**mesma área de RBAC** — usa Global em todas as suas 9 ações (`:18, :30, :38, :50, :60,
:68, :82, :90, ...`).

> **Sintoma:** um administrador de cliente que consegue gerenciar *usuários* e *grupos*
> (AccessList) **não consegue nem listar as roles** (Global) — recebe 403 ao abrir a aba de
> papéis, apesar de a interface de grupos estar funcional. São telas do mesmo módulo de
> administração, uma do lado da outra no menu.

---

## 3. Priorização sugerida

| # | Achado | Local | Sintoma | Gatilho |
|---|---|---|---|---|
| 1 | Download de gravação de terminal sem credencial | `RemoteSessionsController.cs:257-280` + `RecordingPlayer.tsx:31,49` | 401 JSON cru / vídeo "Falha ao carregar" | Sempre |
| 2 | Download de gravação (sessão) idem, + `sessionId` ausente na URL | `RemoteSessionsController.cs:224` + `useRecording.ts:26` | 401 e/ou 404 | Sempre |
| 3 | Energia em massa do site exige Global | `SitesController.cs:191,202,213` | 403 para admin escopado | Sempre |
| 4 | `recording/download` Global vs `terminal/download` AccessList | `RemoteSessionsController.cs:224` vs `:258` | 403 intermitente por tela | Sempre p/ escopado |
| 5 | `RolesController` Global vs Users/UserGroups AccessList | `RolesController.cs:18+` | 403 na aba de papéis | Sempre p/ escopado |
| 6 | 401 sem log, sem path e sem contexto | `AuthFilters.cs:251-255` | dificulta todo diagnóstico acima | — |

> **Removidos da priorização após verificação:** anexos de ticket/chat, relatórios e imagens
> da KB. Todos usam o padrão correto (`apiFetchResponse` + Blob) e **não** sofrem 401
> (§1.6). Permanecem aqui apenas como referência do padrão que o player de gravação
> deveria seguir.

---

## 4. Ressalvas metodológicas

- Contagens por grep sobre `src/`: 561 atributos, 479 Global, 82 explícitos
  (58 AccessList + 24 FromRoute), 55 controllers. Verificado que **nenhum** atributo está
  quebrado em múltiplas linhas, o que tornaria a contagem imprecisa.
- **Não executei o sistema.** Os sintomas são derivados de leitura de código, não de
  reprodução. Onde a conclusão dependia de qual caminho o front usa (`href` vs
  `apiFetch`+Blob), eu **fui verificar cada um** em vez de deixar como suposição — e isso
  derrubou 5 dos 8 itens da minha primeira lista (ver a correção em §1.6). O que sobra como
  risco real de 401 é o caminho do `RecordingPlayer`/`useRecording`, que é o único que
  monta URL crua e abre por navegação direta.
- O 401 de `RemoteSession.popup` já tem teste de reprodução no próprio repositório
  (`src/api/remoteSessions401Repro.test.ts`, `src/auth/storageRegimes.test.ts`) — útil como
  ponto de partida para os demais casos. Esse teste cita o HAR do incidente de 2026-11-07
  (`remoteSessions401Repro.test.ts:9-12`) com `POST /api/v1/remote-sessions/{agentId} -> 401`
  e **sem header Authorization**, confirmando o mecanismo da classe A em produção.
- Achados de escopo assumem a semântica provada na auditoria anterior
  (`IsScopeMatch` descendente; `AuthFilters.cs:198` fixando Global).
- Um 401 de navegação direta é **indistinguível**, para o usuário, de sessão expirada — mas
  recarregar a página (que reidrata o token) **não** resolve, o que é a pista de que se
  trata de header ausente e não de token inválido.
