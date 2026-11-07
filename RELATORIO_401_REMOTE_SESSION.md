# Relatório — HTTP 401 em `POST /api/v1/remote-sessions/{agentId}`

**Incidente:** 2026-11-07 — usuário administrador `pedrostefanogv` não inicia acesso remoto.
**Sintoma:** `POST https://discovery.pstefano.com.br/api/v1/remote-sessions/01a189d0-8076-7ddd-b55e-d7ca6054e5ab` → **401** `{"message":"Autenticação necessária."}` (41 bytes).
**Evidência do HAR:** a requisição **não envia header `Authorization`**; o cookie `cf_clearance` (Cloudflare) está presente.

> Escopo desta investigação: rastrear a origem do 401. **Nenhum arquivo de código-fonte foi alterado.** Este relatório é o único artefato produzido.
> Método: Codebase Memory MCP (`project: RMM-API` e `project: RMM-Site`) antes da leitura de arquivos, conforme `AGENTS.md`.

---

## 1. Veredito

**Causa raiz (PROVADA):** a janela *popup* de acesso remoto (`/agents/remote-session`) executou a chamada **sem token em memória**, então o cliente HTTP enviou o POST **sem header `Authorization`**. Sem header, o `UserAuthMiddleware` nunca popula `HttpContext.Items["UserId"]`, e o filtro `RequirePermissionFilter` devolve **exatamente** `401 {"message":"Autenticação necessária."}`.

**Não é problema de role/permissão do `pedrostefanogv`.** Falha de permissão produziria **403** com outra string (ver §5).
**Não é sessão revogada.** O 401 de `session_revoked` tem corpo e tamanho diferentes (ver §4).

| Hipótese da task-1 | Situação |
|---|---|
| (a) front-end não enviou `Authorization` | **CONFIRMADA — é a causa.** Provada por teste executado (§3.3) |
| (b) token enviado mas rejeitado no `UserAuthMiddleware` | **REFUTADA** — o corpo do 401 do middleware é diferente (§4) |
| (c) rota não passa pelo middleware / ordem do pipeline | **REFUTADA** — a ordem está correta e a rota é coberta (§4) |

---

## 2. Cadeia causal completa (arquivo:linha)

### 2.1 Back-end — de onde o corpo do 401 vem exatamente

1. `src/Discovery.Api/Program.cs:573` — `app.UseUserAuth();` registrado **antes** de `app.MapControllers();` (`Program.cs:575`). A rota é coberta.
2. `src/Discovery.Api/Middleware/UserAuthMiddleware.cs:43` — `var authHeader = context.Request.Headers.Authorization.FirstOrDefault();`
   Com o header ausente, `authHeader` é `null`; o bloco `if` de `UserAuthMiddleware.cs:65-169` **não é executado**; `Items["UserId"]` fica **não populado**.
3. `UserAuthMiddleware.cs:171` — `await _next(context);` **sempre** roda. O middleware **não bloqueia** requisição sem header (comentário explícito em `UserAuthMiddleware.cs:12`). Logo, **o 401 não vem daqui**.
4. `src/Discovery.Api/Controllers/RemoteSessionsController.cs:69-70` — `[HttpPost("{agentId:guid}")]` + `[RequirePermission(ResourceType.Agents, ActionType.Execute)]`.
5. `src/Discovery.Api/Filters/AuthFilters.cs:251-255` — o filtro roda **antes** da action:

   ```csharp
   if (context.HttpContext.Items["UserId"] is not Guid userId)
   {
       context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
       return;
   }
   ```

   **Esta é a string exata do incidente.** O corpo tem 41 bytes: `{"message":"Autenticação necessária."}` — 38 caracteres, dos quais 3 acentuados (ç, á, ó) ocupam 2 bytes cada em UTF-8 → 38 + 3 = **41 bytes**, batendo exatamente com o HAR.

> Nota de precisão: `RemoteSessionsController.cs:73-79` tem um `Unauthorized` *de guarda* dentro da própria action, mas ele emite `{"error":"User not authenticated."}` — **não** é essa a string observada. Além disso o filtro (`AuthFilters.cs:251`) roda antes da action, então esse ramo nem é alcançado. Confirmado que o 401 observado é o **do filtro**.

### 2.2 Front-end — por que o header não foi enviado

6. `DiscoveryRMM_Site/src/api/client.ts:378-385` — o header só é anexado se houver token:

   ```ts
   const useAuth = init.auth !== false;
   if (useAuth && !headers.has("Authorization")) {
     const accessToken = apiClientConfig.getAccessToken();
     if (accessToken) { headers.set("Authorization", `Bearer ${accessToken}`); }
   }
   ```

   Retornando `null` **ou `''`**, a requisição sai **sem** `Authorization` — não há `else` que force erro.
7. `DiscoveryRMM_Site/src/api/remote-sessions.ts:152-153` — `startSession` usa `api.post(...)` **sem** `retryOnAuthError: false`, portanto o caminho de retry é elegível.
8. `DiscoveryRMM_Site/src/pages/agents/RemoteSession.tsx:229-230` — a popup **sobrescreve a configuração global** do cliente com `getAccessToken: () => accessTokenRef.current`.
9. `RemoteSession.tsx:218-219` — `const legacyAccessToken = searchParams.get('accessToken') ?? '';` / `const accessTokenRef = useRef(legacyAccessToken);` → **a ref nasce `''`** quando a URL não traz token.
10. `DiscoveryRMM_Site/src/pages/agents/remoteSessionLauncher.ts:43-45` — a URL da popup só carrega `agentId`. O token **deixou de ir na query string** (auditoria de 2026-11-07, comentário em `remoteSessionLauncher.ts:37-42`), então o caminho legado `accessToken` está morto no fluxo novo.
11. `RemoteSession.tsx:308-309` — sem token legado, a popup publica `postCrossTabMessage({ type: 'TOKEN_REQUEST' })`.
12. `DiscoveryRMM_Site/src/auth/crossTabSync.ts:96-98` — `postCrossTabMessage` **sempre** carimba `from: CROSS_TAB_INSTANCE_ID` (`crossTabSync.ts:36-47`), e `CROSS_TAB_INSTANCE_ID` é criado no **escopo do módulo**, com fallback caso `crypto.randomUUID` não exista. Ou seja: **`from` nunca sai `undefined` a partir deste caminho** — a preocupação (a) do Lead sobre "TOKEN_REQUEST sem from" **não se sustenta pelo código da popup atual**.
13. `DiscoveryRMM_Site/src/auth/AuthContext.tsx:404-438` — a aba principal só responde se **todas** as condições valerem:
    - `AuthContext.tsx:414-416` — `if (!message.from) break;` (não é o caso, ver item 12);
    - `AuthContext.tsx:420` — se faltam <5 min para expirar **e** existe `refreshTokenRef`, delega para `refreshSession()` (assíncrono) e **não responde na hora**;
    - `AuthContext.tsx:425-437` — caso contrário só responde se `stage === "authenticated"` **e** `accessToken` **e** `refreshToken` **e** `expiresAt` estiverem todos preenchidos. **Qualquer um ausente ⇒ nenhum ramo responde.**
14. `crossTabSync.ts:104-110` — `replyTokensTo` retorna **cedo e silenciosamente** se `requesterId` for falsy (defesa da auditoria: "um pedido anônimo não recebe credenciais").
15. `RemoteSession.tsx:314-316` — **race:** após **5 s** sem resposta, `tokenReadyRef.resolve(accessTokenRef.current || null)` → resolve com `null`, liberando a inicialização.
16. `RemoteSession.tsx:352` — a inicialização faz `await tokenReadyRef.current?.promise;` e em seguida dispara `agentsApi.get(agentId)` (`RemoteSession.tsx:355`). Esse **é** o request que aparece no HAR sem `Authorization`.
17. `DiscoveryRMM_Site/src/router.tsx:169-181` — `/agents/remote-session` está sob `RequireAuth` (`router.tsx:171`) mas **fora** do `MainLayout` (`router.tsx:197`), exatamente como as demais janelas dedicadas. Confirma que é uma janela própria, com o mesmo `AuthContext` mas **sem** o token que a aba principal tem — e como `RequireAuth` valida via `sessionStorage`/`session` local da própria janela, a popup **não** redireciona para login; ela segue e falha na API.

### 2.3 Por que o retry de 401 não salva o request

18. `client.ts:395-416` — ao receber 401 o cliente tenta `refreshAccessToken()`.
19. `RemoteSession.tsx:231-261` — na popup, `refreshAccessToken` **espera 3 s** por um `TOKEN_REFRESHED` (`RemoteSession.tsx:247-258`). Se o handshake nunca respondeu, ele resolve `null` (`RemoteSession.tsx:254-256`), e então `client.ts:397-400` chama `onAuthFailure()` e **devolve o 401 original**.

**Conclusão:** o retry depende do **mesmo** mecanismo cross-tab que falhou. Ele não é uma segunda chance real — é o mesmo caminho, com 3 s extras.

---

## 3. Evidências empíricas

### 3.1 Corpo do 401 — conferência de bytes

```
{"message":"Autenticação necessária."}                                  → 41 bytes  ← HAR (41 bytes) ✔
{"message":"Sessão inválida ou revogada. Faça login novamente.","code":"session_revoked"} → 92 bytes  ✘
{"message":"Permissão insuficiente."}                                   → 38 bytes  ✘
{"message":"Permissão insuficiente para este escopo."}                  → 55 bytes  ✘
```

O HAR diz **41 bytes** e a string bate literalmente com `AuthFilters.cs:253`. Isso **descarta** o 401 do `UserAuthMiddleware` e qualquer erro de permissão.

### 3.2 Teste de regressão já existente no repositório

`DiscoveryRMM_Site/src/api/remoteSessions401Repro.test.ts` (98 linhas) foi escrito para este incidente e declara a mesma causa em `remoteSessions401Repro.test.ts:1-16`. Ele cobre exatamente os três estados:

- `remoteSessions401Repro.test.ts:43-63` — `getAccessToken: () => null` ⇒ o POST sai **sem** `Authorization` e o corpo é `{ message: "Autenticação necessária." `;
- `remoteSessions401Repro.test.ts:83-97` — **token vazio `''`** (que é o valor inicial de `accessTokenRef` em `RemoteSession.tsx:219`) ⇒ também sai **sem** `Authorization` — caso decisivo para este incidente;
- `remoteSessions401Repro.test.ts:65-81` — com token ⇒ envia `Bearer` (contraprova).

### 3.3 Execução (verificação independente, por mim)

```
$ npx vitest run src/api/remoteSessions401Repro.test.ts
 ✓ src/api/remoteSessions401Repro.test.ts (3 tests) 7ms
 Test Files  1 passed (1)
      Tests  3 passed (3)
```

**Provado, não inferido:** a ausência de token produz request sem header, e o token `''` produz o mesmo efeito. A parte do cliente da cadeia está fechada.

### 3.4 Cobertura de teste do handshake cross-tab — LACUNA

Não existe teste cobrindo o caminho **popup-sem-token / `TOKEN_REQUEST`**. Verificado:

- Não há `src/pages/agents/**/*.test.tsx` (só 196 arquivos de teste no total; nenhum para `remoteSessionLauncher` ou `RemoteSession.tsx`);
- `AuthContext.test.tsx` **não contém nenhuma referência** a `TOKEN_REQUEST`, `replyTokensTo` ou `from`;
- `src/**/*.test.ts*` não tem nenhum teste para `crossTabSync.ts`.

Ou seja: o `remoteSessions401Repro.test.ts` prova que **sem token o header não vai**, mas **nada** prova *por que* a popup ficou sem token — é justamente o elo descoberto. Vale registrar como risco: a correção de segurança de 2026-11-07 (endereçar `TOKEN_REFRESHED` e remover o token da URL) **não veio acompanhada de teste do handshake**.

---

## 4. Hipóteses refutadas, com prova

### (b) REFUTADA — não é token rejeitado / `session_revoked`

Se um `Authorization: Bearer` tivesse chegado e sido rejeitado, o `UserAuthMiddleware` teria respondido por `UserAuthMiddleware.cs:136-142` com:

```json
{"message":"Sessão inválida ou revogada. Faça login novamente.","code":"session_revoked"}
```

Isso são **92 bytes** e uma string completamente diferente da do HAR (41 bytes). Além disso, o HAR **não tem header `Authorization`** — para haver rejeição, o header teria de existir. As duas condições juntas eliminam esta hipótese.

*(O código de fato tem um mecanismo de grace para rotação — `UserAuthMiddleware.cs:118-131` — mas ele só é alcançado com header presente.)*

### (c) REFUTADA — ordem do pipeline está correta

`Program.cs:561-575`:

```
561  app.UseMiddleware<ExceptionHandlingMiddleware>();
563  app.UseRateLimiter();
564  app.UseOutputCache();
565  app.UseCors("DefaultApi");
566  app.UseSecurityHeaders();
569  app.UseAgentAuth();
572  app.UseApiTokenAuth();
573  app.UseUserAuth();        ← antes de MapControllers
575  app.MapControllers();
```

`UseUserAuth` está registrado **antes** de `MapControllers`, sem `UseRouting` intermediário que pudesse particionar o pipeline, e sem `UseWhen`/branch por path. `/api/v1/remote-sessions/*` **está coberto**. A única lista de exceção é `_publicPaths` (`UserAuthMiddleware.cs:17-21`) com apenas `/api/v1/auth/login` e `/api/v1/auth/refresh` — a rota do incidente não está nela.

### Descartado — rate limit, CORS e nginx removendo o header

- **Rate limit:** `RemoteSessionsController.cs` não tem atributo algum de rate limiting, e `Program.cs` não define política nomeada com `PermitLimit`. Ainda que houvesse, `UseRateLimiter` (`Program.cs:563`) roda **antes** de `UseUserAuth` e devolveria **429**, não 401. O body do 401 no HAR corresponde ao filtro, não a um limiter.
- **CORS:** a política `DefaultApi` (`CorsServiceCollectionExtensions.cs:25-26`, aplicada em `Program.cs:565`) usa `AllowAnyHeader()` (`CorsServiceCollectionExtensions.cs:40,46`) — CORS **não** remove `Authorization`. Note-se que CORS é irrelevante aqui: a página é **same-origin** (`discovery.pstefano.com.br` → `/api/...`), não é cross-origin.
- **nginx:** o template `scripts/linux/templates/nginx-discovery.conf.tpl:93-101` faz `proxy_pass` para o backend com `proxy_set_header` apenas de `Connection`, `Host`, `X-Real-IP`, `X-Forwarded-For`, `X-Forwarded-Proto`. **Não há** `proxy_set_header Authorization ""` nem `proxy_hide_header` em nenhum ponto do arquivo — o nginx **repassa** `Authorization` por padrão. E, decisivo: o HAR registra a requisição **sem** `Authorization`, então não houve header a ser descartado — o problema é **a montante** do proxy.
- **Cloudflare / `cf_clearance`:** é cookie de challenge de bot, não credencial de aplicação. Não é lido em nenhum ponto do back-end (`UserAuthMiddleware` só olha `Headers.Authorization` e, para `/nats`, `access_token` na query — `UserAuthMiddleware.cs:43-63`). É **irrelevante** para este 401, e sua presença apenas confirma que a requisição passou pelo Cloudflare.

---

## 5. Prova de que 401 ≠ problema de role (pedido 4 da task)

O endpoint exige `ResourceType.Agents` + `ActionType.Execute` (`RemoteSessionsController.cs:70`). O atributo usa **escopo Global por padrão** (`AuthFilters.cs:194-199`, documentado em `AuthFilters.cs:184-186`).

No `RequirePermissionFilter`, os dois desfechos são **estruturalmente distintos**:

| Condição | Resultado | Arquivo:linha |
|---|---|---|
| `Items["UserId"]` **não** é `Guid` (sem auth) | **401** `{"message":"Autenticação necessária."}` | `AuthFilters.cs:251-255` |
| `UserId` presente, mas sem a permissão | **403** `{"message":"Permissão insuficiente."}` | `AuthFilters.cs:262-269` |
| `UserId` presente, sem permissão em escopo `FromRoute` | **403** `{"message":"Permissão insuficiente para este escopo."}` | `AuthFilters.cs:305-312` |

O usuário é **administrador** e a falha ocorre na **primeira** verificação (identidade), nunca chegando a `HasPermissionAsync` (`AuthFilters.cs:259-260`). Portanto: **o 401 não diz nada sobre as permissões do `pedrostefanogv`** — conceder mais roles não mudaria nada. Isso também é coerente com o HAR: a requisição sequer apresentou identidade.

---

## 6. O que NÃO pode ser a causa (lista negativa)

1. **Permissões/role do `pedrostefanogv`** — produziria 403 (`AuthFilters.cs:264-268`). Administrador ou não, é irrelevante: o filtro para antes de consultar permissão.
2. **Sessão revogada / access token obsoleto** — produziria 401 com `code: "session_revoked"` e 92 bytes (`UserAuthMiddleware.cs:136-142`); o HAR tem 41 bytes e não há header para validar.
3. **Ordem do middleware / rota descoberta** — `Program.cs:573` antes de `Program.cs:575`; a rota não está em `_publicPaths` (`UserAuthMiddleware.cs:17-21`).
4. **`UserAuthMiddleware` bloqueando requisição sem header** — ele **nunca** bloqueia; `UserAuthMiddleware.cs:171` chama `_next` incondicionalmente. O 401 vem do **filtro**.
5. **Rate limit** — devolveria 429 e roda antes; não há política no controller.
6. **CORS** — `AllowAnyHeader` (`CorsServiceCollectionExtensions.cs:46`); requisição é same-origin de qualquer forma.
7. **nginx removendo `Authorization`** — nenhum `proxy_set_header Authorization ""` no template; e não havia header a remover.
8. **Cookie `cf_clearance` / Cloudflare** — não é lido como credencial em nenhum ponto do back-end.
9. **Código do controller `StartSession`** — nunca executou; o filtro curto-circuita em `AuthFilters.cs:251-255`.
10. **MFA pendente (`mfa_pending`)** — não se aplica: sem header não há token algum cujo claim `mfa_pending` fosse lido (`UserAuthMiddleware.cs:86`).

---

## 7. Hipótese mais provável para o *porquê* do handshake falhar

O back-end está **descartado** como culpado: ele se comporta corretamente diante de um request sem identidade. A pergunta remanescente é **por que a popup não recebeu o token**. As três variantes possíveis, ordenadas por força da evidência:

**H1 — a aba principal não tinha sessão respondível no instante do `TOKEN_REQUEST` (mais provável).**
`AuthContext.tsx:414-437` só responde sob condições estritas. Se a sessão estava em `mfa_pending`, ou sincronizando com o servidor após um reload, ou com `refreshToken`/`expiresAt` transitoriamente ausentes, **nenhum ramo responde** e a popup espera em vão até os 5 s de `RemoteSession.tsx:314-316` resolverem com `null`. Note que o ramo de refresh (`AuthContext.tsx:420-424`) é assíncrono: ele registra o solicitante em `pendingTokenRequestersRef` e só responde quando o refresh termina (`AuthContext.tsx:134-139`) — se o refresh falhar ou demorar mais que a janela da popup, a resposta nunca chega.

**H2 — o `BroadcastChannel` não entregou a mensagem entre as duas janelas (bastante plausível).**
`crossTabSync.ts:33` usa `BroadcastChannel('discovery:auth:v1')`, escopado por **origem**. Ambos os `postCrossTabMessage` da popup e o listener da aba principal dependem de o canal estar operacional nas duas pontas. Em cenários reais de popup — janela em processo/partição distinta, `BroadcastChannel` indisponível ou bloqueado por política, ou bundle antigo em cache na popup antes da mudança de fluxo — a mensagem some **sem erro visível**, e nada no código detecta esse silêncio: `RemoteSession.tsx:308-316` não diferencia "sem resposta" de "sem aba principal".
> Observação céptica sobre a variante (a) do Lead: **`from` undefined não é um cenário viável pelo caminho atual**, porque `postCrossTabMessage` (`crossTabSync.ts:96-98`) **sempre** sobrescreve `from` com `CROSS_TAB_INSTANCE_ID` — que é criado no escopo do módulo (`crossTabSync.ts:36`) com fallback determinístico (`crossTabSync.ts:46`). Um `from` ausente exigiria **outro emissor** (bundle antigo que publique sem carimbar, ou script injetado) — é justamente o ataque que a auditoria endereçou. Ou seja: se o `from` estava ausente, é sinal de **janela com bundle desatualizado**, não de bug no caminho atual.

**H3 — a mudança de 2026-11-07 introduziu o modo de falha.**
Antes, o token ia na query string (`RemoteSession.tsx:218` mantém o parâmetro apenas por compatibilidade) e a popup **sempre** tinha token. Ao removê-lo (`remoteSessionLauncher.ts:37-45`), o token passou a depender **inteiramente** de um handshake assíncrono não testado (§3.4). **Isto é confirmado por construção**: o fallback `legacyAccessToken` (`RemoteSession.tsx:218`) está vazio no fluxo novo, então o único caminho para haver token é o cross-tab. **Correlação temporal forte com o incidente.**

### Discriminação entre H1/H2/H3 — o que ainda NÃO está provado

Não é possível decidir entre H1 e H2 apenas com o código: ambos produzem **exatamente** o mesmo sintoma observável (popup sem token → request sem `Authorization` → 401 do filtro). O HAR sozinho não distingue. Para fechar a questão, os dados decisivos seriam:

- **Console da popup** no momento da falha: presença de erros de `BroadcastChannel`, e o resultado de `crypto.randomUUID` (define se `CROSS_TAB_INSTANCE_ID` veio do fallback);
- **`AuthContext` da aba principal** no instante do pedido: valor de `stage`, presença de `refreshToken`/`expiresAt`;
- **Log do back-end**: `RemoteSessionsController.cs:76-77` emite `LogWarning` — mas **não** nesse caso, porque o filtro curto-circuita antes da action. Portanto **não espere log do controller**; a ausência dele é esperada e **não** indica que a requisição não chegou.
- Um `DevTools` na popup confirmando se `TOKEN_REFRESHED` chegou após o `TOKEN_REQUEST`.

**O que está PROVADO:** o 401 vem do filtro, porque o request saiu sem `Authorization`, porque o provedor de token da popup devolvia vazio (teste §3.3).
**O que é HIPÓTESE:** qual dos mecanismos (H1/H2/H3) impediu o token de chegar.
**O que está REFUTADO:** role, sessão revogada, ordem de middleware, rate limit, CORS, nginx, Cloudflare.

---

## 8. Encaminhamentos sugeridos (não executados — fora do meu escopo de escrita)

> Nenhuma alteração de código foi feita. Itens para o time decidir:

1. **Instrumentar o silêncio:** o ponto mais frágil é `RemoteSession.tsx:308-316` não distinguir "sem aba principal" de "aba principal não respondeu". Um log/timeline do `TOKEN_REQUEST` tornaria H1 vs H2 observável.
2. **Teste do handshake:** criar teste para o caminho `TOKEN_REQUEST` → `TOKEN_REFRESHED` envolvendo `crossTabSync` e `AuthContext` — hoje **inexistente** (§3.4).
3. **Fail-fast na popup:** quando `tokenReadyRef` resolve `null`, a popup poderia exibir "não foi possível obter credenciais da aba principal" em vez de deixar o operador ver um 401 opaco.
4. **Log do filtro:** `AuthFilters.cs:251-255` devolve 401 **sem log**. Um `LogWarning` ali (com path) tornaria esse cenário diagnosticável pelo back-end — hoje é invisível nos logs do servidor.
5. **Compatibilidade de bundle:** se H2 for causado por popup com bundle em cache, vale versionar/assegurar o carregamento do bundle na popup.

---

## 9. Metodologia e limites

- Consultas ao Codebase Memory MCP antes da leitura de arquivos, conforme `DiscoveryRMM_API/AGENTS.md` e `DiscoveryRMM_Site/AGENTS.md`: `get_architecture` (RMM-API: 34.970 nós/169.712 arestas; RMM-Site: 9.287 nós/34.850 arestas), `search_graph`, `get_code_snippet`, `trace_path`.
- Leitura direta autorizada por `AGENTS.md` para conteúdo exato de linha.
- Verificação executada: `npx vitest run src/api/remoteSessions401Repro.test.ts` → **3/3 passaram** (§3.3).
- Cálculo de bytes dos candidatos a corpo de erro feito por medição UTF-8, não por estimativa (§3.1).
- **Limite declarado:** não houve acesso ao ambiente de produção (logs do servidor, console do navegador, `DevTools` da popup) nem aos arquivos do HAR. A distinção final entre H1 e H2 depende desses dados.
- **Escopo de escrita respeitado:** apenas este arquivo foi criado. Nenhum arquivo de código-fonte foi modificado.
