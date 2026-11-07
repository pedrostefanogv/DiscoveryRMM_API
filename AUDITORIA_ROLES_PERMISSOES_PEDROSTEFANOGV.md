# Auditoria de Roles e Permissões — usuário `pedrostefanogv`

**Pergunta da investigação:** após os "ajustes de permissão de usuário", o administrador
`pedrostefanogv` perdeu o acesso remoto. Este documento mapeia o modelo de RBAC, explica
por que uma role de escopo Client/Site **não** satisfaz o `StartSession`, e entrega as
queries SQL prontas para o operador validar no Postgres.

Escopo: apenas leitura de código. Nenhum arquivo-fonte foi alterado.

---

## 1. Modelo de dados (UserGroup → … → Permission)

A cadeia de concessão é **usuário → grupo → atribuição de role (com escopo) → role →
role_permissions → permissions**.

Nomes físicos confirmados no mapeamento EF (`DiscoveryDbContext.Identity.cs`):

| Entidade | Tabela | Arquivo:linha |
|---|---|---|
| `User` | `users` | `src/Discovery.Infrastructure/Data/DiscoveryDbContext.Identity.cs:16` |
| `UserGroup` | `user_groups` | `...DiscoveryDbContext.Identity.cs:42` |
| `UserGroupMembership` | `user_group_memberships` | `...DiscoveryDbContext.Identity.cs:54` |
| `Role` | `roles` | `...DiscoveryDbContext.Identity.cs:63` |
| `Permission` | `permissions` | `...DiscoveryDbContext.Identity.cs:78` |
| `RolePermission` | `role_permissions` | `...DiscoveryDbContext.Identity.cs:89` |
| `UserGroupRole` | `user_group_roles` | `...DiscoveryDbContext.Identity.cs:102` |

Colunas relevantes (todas em `DiscoveryDbContext.Identity.cs`):

- `user_groups`: `id`, `name`, **`is_active`** (:47)
- `user_group_memberships`: `user_id`, `group_id`, `joined_at` (:56-58) — PK composta `(user_id, group_id)` (:55)
- `roles`: `id`, `name`, `type`, `is_system`, **`is_active`**, `mfa_requirement` (:66-71)
- `permissions`: `id`, **`resource_type`**, **`action_type`**, `description` (:81-83)
- `role_permissions`: `role_id`, `permission_id` (PK composta, :90)
- `user_group_roles`: `id`, `group_id`, `role_id`, **`scope_level`**, **`scope_id`**, `assigned_at` (:104-109)

> **Importante para quem for escrever SQL:** `resource_type`, `action_type`,
> `scope_level`, `roles.type` e `roles.mfa_requirement` usam `HasConversion<string>()`
> (DbContext :81, :82, :107; :68, :70). No banco são **texto**, não inteiro — por isso as
> queries usam `= 'Agents'` e `= 'Execute'` entre aspas.

### Enums

- `ResourceType` — `src/Discovery.Core/Enums/Identity/ResourceType.cs:3-29`; `Agents` é o **primeiro** membro (:5).
- `ActionType` — `src/Discovery.Core/Enums/Identity/ActionType.cs:3-10`; ordem `View, Create, Edit, Delete, Execute` — `Execute` é o **último** (:9).
- `ScopeLevel` — `src/Discovery.Core/Enums/Identity/ScopeLevel.cs:9-14`: `Global, Client, Site`.
- `ScopeSource` — `src/Discovery.Core/Enums/Identity/ScopeSource.cs:6-19`: `Global, FromRoute, AccessList`.

### Seed das permissões (Agents:Execute existe desde a M069)

`src/Discovery.Migrations/Migrations/M069_CreateUsersIdentity.cs:124-137` cria o produto
cartesiano `ResourceType × ActionType` — `Agents` na lista de recursos (:129) e `Execute`
na lista de ações (:135), gerando `Agents:Execute` com `description = 'Agents:Execute'` (:127).

`M069_CreateUsersIdentity.cs:140-146` concede **todas** as permissões à role `Admin`
(`is_system = true`). Ou seja: uma role `Admin` íntegra **tem** `Agents:Execute`.

Gap corrigido depois: a M069 criava as roles, mas **nenhuma migration criava o grupo**
`Administradores` — documentado em `M118_SeedDefaultAdminGroup.cs:8-11`. A
`M118_SeedDefaultAdminGroup.cs:25-79` cria o grupo, vincula o **primeiro usuário ativo**
(`ORDER BY created_at LIMIT 1`, :37) e atribui `Admin` com `scope_level='Global'`,
`scope_id=NULL` (:56). A `M083_HardenInitialAdminSeed.cs:46-71` faz o mesmo para o login
literal `admin`.

> **Nota de risco já presente no código:** o vínculo da M118 é por *ordem de criação do
> usuário ativo*, não por login (:37). Se `pedrostefanogv` não for o primeiro usuário ativo
> da base, ele **nunca** recebeu a role Admin por essa migration — a concessão veio de um
> ajuste manual, que é exatamente o que pode ter sido mexido.

### Migration que introduziu o "estado desativável" (suspeito principal)

`M195_AddIdentityAuthHardening.cs:19-23` adiciona `roles.is_active` com default `true`,
descrito no cabeçalho como "permite desativar uma role sem excluí-la (contrato do console
web)" (:8). **É essa a coluna que os "ajustes de permissão" passaram a poder alterar.**

---

## 2. Query que resolve as permissões (o filtro silencioso)

Repositório: `src/Discovery.Infrastructure/Repositories/UserGroupRepository.cs:110-133`.

A consulta exige **três** condições simultâneas:

1. **Grupo ativo** — `_db.UserGroups.Where(g => g.IsActive)` (:115)
2. **Membership viva** — `_db.UserGroupMemberships.Where(m => m.UserId == userId && activeGroupIds.Contains(m.GroupId))` (:117-118)
3. **Role ativa** — `.Where(x => x.Role.IsActive)` (:122)

O comentário do próprio método (:111-114) é explícito:

> "Grupos e roles inativos deixam de conceder permissões (contrato do console web: a role
> pode ser desativada sem ser excluída). Antes, o filtro não existia e uma role 'inativa'
> continuava autorizando todas as ações."

**Consequência operacional:** se o grupo OU a role OU a membership estiver inativo(a), o
método retorna **lista vazia sem erro, sem log e sem exceção**. O usuário passa a ter zero
permissões e o sintoma aparece como `403 "Permissão insuficiente."` — nunca como falha de
autenticação. É a falha silenciosa descrita na task.

---

## 3. Por que escopo Client/Site NÃO satisfaz o `StartSession`

O endpoint é `src/Discovery.Api/Controllers/RemoteSessionsController.cs:68-71`:

```csharp
/// <summary>Inicia uma nova sessão de acesso remoto.</summary>
[HttpPost("{agentId:guid}")]
[RequirePermission(ResourceType.Agents, ActionType.Execute)]
public async Task<IActionResult> StartSession(Guid agentId, ...)
```

O atributo **não** informa `ScopeSource`, então vale o default do construtor:
`ScopeSource.Global` — `src/Discovery.Api/Filters/AuthFilters.cs:240`
(`RequirePermissionFilter(..., ScopeSource scopeSource = ScopeSource.Global)`).

Com `ScopeSource.Global`, o filtro chama (`AuthFilters.cs:257-259`):

```csharp
var hasPermission = await _permissionService.HasPermissionAsync(
    userId, _resource, _action, ScopeLevel.Global, null, null);
```

Ou seja: **`requestedScope = Global`, `requestedScopeId = null`, `parentScopeId = null`**.
Falhando, devolve `403` com `{ message = "Permissão insuficiente." }` (:262-266).

### `IsScopeMatch` — a razão exata da recusa

`src/Discovery.Infrastructure/Services/PermissionService.cs:170-195`:

```csharp
if (assignmentScope == ScopeLevel.Global)
    return true;                                                    // :178-179

if (assignmentScope == ScopeLevel.Client && assignmentScopeId.HasValue)
{
    if (requestedScope == ScopeLevel.Client && requestedScopeId == assignmentScopeId)
        return true;
    if (requestedScope == ScopeLevel.Site && parentScopeId == assignmentScopeId)
        return true;                                                // :181-188
}

if (assignmentScope == ScopeLevel.Site && assignmentScopeId.HasValue)
{
    if (requestedScope == ScopeLevel.Site && requestedScopeId == assignmentScopeId)
        return true;                                                // :190-194
}

return false;                                                       // :194
```

A herança de escopo **é descendente, nunca ascendente**: Global alcança tudo, Client alcança
seu próprio client e seus sites, Site alcança só a si mesmo. Não existe o caminho inverso
(uma atribuição Client satisfazer uma requisição Global).

Aplicando ao caso — atribuição `Client`/`Site` com `requestedScope = Global`:

- ramo `Client` (:181): exige `requestedScope == Client` **ou** (`requestedScope == Site` e
  `parentScopeId == assignmentScopeId`). `Global` não casa com nenhum → falha.
- ramo `Site` (:190): exige `requestedScope == Site`. `Global` não casa → falha.
- cai no `return false` (:194).

Como o `HasPermissionAsync` faz `continue` quando `IsScopeMatch` é falso
(`PermissionService.cs:36-37`), a permissão é ignorada mesmo que `Agents:Execute`
**exista** no conjunto da role.

> **Diagnóstico diferencial central:** se os "ajustes" mudaram a atribuição de
> `scope_level='Global'/scope_id=NULL` para `Client` ou `Site`, o usuário mantém a
> permissão `Agents:Execute` no catálogo (a query de `role_permissions` continua
> retornando a linha!) e **ainda assim** perde o acesso remoto. Verificar só
> "tem a permissão?" dá falso negativo. **É preciso checar o escopo junto.**

Contraste com `ScopeSource.AccessList` (usado, por ex., em
`AgentAlertsController.cs:116`): nesse modo o filtro aceita `AllowedClientIds.Count > 0`
(`AuthFilters.cs:275-277`) e **não** exige Global. É por isso que o mesmo usuário pode
manter *algumas* telas funcionando e falhar só no acesso remoto — assinatura típica de
mudança de escopo (ou de permissão), não de conta desativada.

---

## 4. Efeito do cache

Implementação: `src/Discovery.Infrastructure/Services/PermissionService.cs`.

Há **dois** níveis de cache, com lifetimes muito diferentes. Distingui-los corretamente é
essencial para a instrução operacional.

### Nível 1 — Redis (o que realmente importa operacionalmente)

- Chave: `"perm:user:" + userId.ToString("N")` — `CachePrefix` em :20, montagem em :112 e :162.
- TTL: `CacheTtl = 300` segundos (**5 minutos**) — :21, aplicado em :162.
- **Sobrevive entre requests e entre instâncias.** Um `UPDATE` direto no banco fica
  invisível por **até 5 minutos** para todos os nós da API.

### Nível 2 — `_userRoleCache` (scoped: vive apenas dentro de uma unidade de trabalho)

- Declarado em `PermissionService.cs:86` como campo de **instância**, não estático:
  `private readonly Dictionary<Guid, IReadOnlyList<RoleAssignmentWithPermissions>> _userRoleCache = new();`
- `IPermissionService` está registrado como **scoped**:
  `src/Discovery.Api/DependencyInjection/ServiceCollectionExtensions.cs:82`
  (`services.TryAddScoped(iface, impls[0])`). A interface **não** está na lista de exclusões
  do registro manual (:55-71), `PermissionService` é a única implementação
  (`PermissionService.cs:11`) e não há registro explícito concorrente — as três condições
  para cair na convenção se cumprem.
- **Consequência:** `_userRoleCache` é criado e descartado junto com o escopo DI. Dentro de
  uma mesma unidade de trabalho ele evita consultas repetidas ao Redis (é consultado antes
  do Redis em `GetUserRolesAsync`, :90-91 na frente de :93), mas **não sobrevive entre
  requests**.

> Precisão sobre o termo "unidade de trabalho": o escopo DI equivale a um request HTTP, mas
> nem todo escopo é um request. O callout de autenticação NATS cria um escopo próprio por
> mensagem (`NatsAuthCalloutBackgroundService.cs:123`, `_scopeFactory.CreateAsyncScope()`)
> e resolve `IPermissionService` ali dentro (:126). Em todos os casos o escopo é curto e
> descartável — o que nunca acontece é o dicionário virar um cache de processo.

Ordem de resolução (`GetUserRolesAsync`, :88-106): `_userRoleCache` (escopo) → Redis
(`perm:user:*`) → banco.

### Por quanto tempo um ajuste fica invisível

- **Se a alteração passou pelos handlers da API:** invalidação imediata — efeito ~0s.
- **Se a alteração foi feita direto no banco (UPDATE manual/SQL):** o valor antigo continua
  valendo por **até 300 s**, por causa do **Redis**. Não há persistência além disso: o
  `_userRoleCache` é scoped e desaparece no fim da unidade de trabalho.

> **Armadilha de diagnóstico:** o operador corrige o banco, testa, e o 403 persiste (ou o
> oposto: ele quebra e o acesso continua funcionando). Enquanto o TTL do Redis não expirar,
> o teste mede o cache, não o banco. **Invalide (ou aguarde os 5 min) antes de concluir
> qualquer coisa.**

### Como invalidar

1. **Via API (caminho correto):** qualquer escrita passa por
   `InvalidateUserCacheAsync` / `InvalidateAllCacheAsync`. Chamadas existentes:
   - `Cqrs/Roles/RoleHandlers.cs:77` (role editada / `is_active` alterado — :66-72)
   - `Cqrs/Roles/RoleHandlers.cs:98` (role excluída)
   - `Cqrs/Roles/RoleHandlers.cs:195` (permissão **adicionada** à role)
   - `Cqrs/Roles/RoleHandlers.cs:227` (permissão **removida** da role)
   - `Cqrs/UserGroups/UserGroupHandlers.cs:192` e `:237` (membro adicionado/removido)
   - `Cqrs/UserGroups/UserGroupHandlers.cs:356` (`UserGroupPermissionInvalidation.InvalidateMembersAsync` — troca de role/escopo do grupo)
   - `Cqrs/Users/UserHandlers.cs:191`, `Cqrs/Support/SupportProductivityHandlers.cs:505` e `:579`
2. **Direto no Redis:** `DEL perm:user:<guid-sem-hifens>` (formato `"N"`, :112).
   Para todos: apagar por prefixo `perm:user:` — é o que `InvalidateAllCacheAsync` faz via
   `DeleteByPrefixAsync(CachePrefix)` (:215).
3. **Não é necessário reiniciar a API.** O `_userRoleCache` é scoped (:86) e morre sozinho
   no fim da unidade de trabalho, então o `DEL` no Redis basta para os próximos requests.
   Um restart continua sendo uma garantia geral inofensiva (e resolve qualquer cache
   `static` alheio a este fluxo), mas **não** é exigido pelo desenho do PermissionService.

> Detalhe: `InvalidateUserCacheAsync` captura a exceção do Redis e segue em frente
> (:200-207), então uma falha de Redis **não** impede a limpeza local — mas, como só o Redis
> é compartilhado, ela também **não** limpa as outras instâncias. Com múltiplos nós, o
> `DEL` no Redis é o único caminho que alcança todos.

---

## 5. Queries SQL de verificação (Postgres, nomes reais do EF)

Rode na base `discovery_dev` (`src/Discovery.Api/appsettings.Development.json:3`).
Todas as queries são somente-leitura.

### Q1 — O usuário existe e está ativo?

```sql
SELECT id, login, email, full_name, is_active,
       failed_login_attempts, lockout_until, mfa_lockout_until, last_login_at
FROM users
WHERE lower(login) = lower('pedrostefanogv');
```

`is_active = false` descarta o problema de roles e aponta para conta desativada.

---

### Q2 — Caminho completo: grupo → membership → role → escopo → permissão

**Esta é a query decisiva.** Mostra, para cada atribuição, se grupo e role estão ativos e
qual o escopo — a informação que o `HasPermissionAsync` usa.

```sql
SELECT
    u.login,
    u.is_active              AS user_active,
    g.name                   AS group_name,
    g.is_active              AS group_active,
    r.name                   AS role_name,
    r.is_system              AS role_is_system,
    r.is_active              AS role_active,
    ugr.scope_level,
    ugr.scope_id,
    CASE
        WHEN ugr.scope_level = 'Global' THEN 'ALCANCE GLOBAL (satisfaz StartSession)'
        WHEN ugr.scope_level = 'Client' THEN 'ESCOPO CLIENT -> NAO satisfaz StartSession'
        WHEN ugr.scope_level = 'Site'   THEN 'ESCOPO SITE   -> NAO satisfaz StartSession'
    END                      AS veredito_escopo,
    ugr.assigned_at
FROM users u
JOIN user_group_memberships ugm ON ugm.user_id = u.id
JOIN user_groups            g   ON g.id = ugm.group_id
JOIN user_group_roles       ugr ON ugr.group_id = g.id
JOIN roles                  r   ON r.id = ugr.role_id
WHERE lower(u.login) = lower('pedrostefanogv')
ORDER BY g.name, r.name, ugr.scope_level;
```

**Como ler o resultado:**

| Sintoma na Q2 | Diagnóstico |
|---|---|
| **0 linhas** | Membership removida ou grupo excluído — nenhuma permissão é concedida |
| Linhas, todas com `group_active = false` | Grupo desativado → `UserGroupRepository.cs:115` filtra tudo |
| Linhas, todas com `role_active = false` | Role desativada (M195 `roles.is_active`) → filtro `:122` |
| Linhas com `scope_level` = `Client`/`Site` e `scope_id` preenchido | **Escopo rebaixado** — causa mais provável; `IsScopeMatch` devolve `false` (`PermissionService.cs:194`) |
| Linha com `scope_level='Global'`, `scope_id IS NULL`, ambos ativos | Escopo OK — investigar Q3/Q4 |

---

### Q3 — A role do usuário tem `Agents:Execute`?

```sql
SELECT DISTINCT
    u.login,
    r.name       AS role_name,
    p.resource_type,
    p.action_type,
    p.description
FROM users u
JOIN user_group_memberships ugm ON ugm.user_id = u.id
JOIN user_group_roles       ugr ON ugr.group_id = ugm.group_id
JOIN roles                  r   ON r.id = ugr.role_id
JOIN role_permissions       rp  ON rp.role_id = r.id
JOIN permissions            p   ON p.id = rp.permission_id
WHERE lower(u.login) = lower('pedrostefanogv')
  AND p.resource_type = 'Agents'
  AND p.action_type   = 'Execute';
```

**0 linhas** = a permissão foi removida da role (ou a role perdeu o vínculo
`role_permissions`). Note que esta query **não** considera escopo nem flags de ativo — de
propósito: combinada com a Q2, isola a causa. Se a Q3 retorna linha **e** a Q2 mostra escopo
Client/Site, o problema é **escopo**; se a Q3 não retorna nada, o problema é **permissão**.

---

### Q4 — Estado "global completo": só retorna linha se o StartSession DEVE passar

Réplica fiel da consulta do `GetRolesWithPermissionsForUserAsync`
(`UserGroupRepository.cs:110-133`) combinada com o escopo Global do `IsScopeMatch`
(`PermissionService.cs:178-179`).

```sql
SELECT DISTINCT
    u.login,
    r.name  AS role_name,
    ugr.scope_level,
    ugr.scope_id,
    p.resource_type || ':' || p.action_type AS permission
FROM users u
JOIN user_group_memberships ugm ON ugm.user_id = u.id
JOIN user_groups            g   ON g.id = ugm.group_id
JOIN user_group_roles       ugr ON ugr.group_id = g.id
JOIN roles                  r   ON r.id = ugr.role_id
JOIN role_permissions       rp  ON rp.role_id = r.id
JOIN permissions            p   ON p.id = rp.permission_id
WHERE lower(u.login)      = lower('pedrostefanogv')
  AND u.is_active         = true          -- users.is_active
  AND g.is_active         = true          -- filtro UserGroupRepository.cs:115
  AND r.is_active         = true          -- filtro UserGroupRepository.cs:122
  AND ugr.scope_level     = 'Global'      -- IsScopeMatch PermissionService.cs:178-179
  AND p.resource_type     = 'Agents'
  AND p.action_type       = 'Execute';
```

- **Retorna 1+ linha** → o banco autoriza; o 403 é **cache** (§4) ou outro fator
  (token/sessão, feature flag `_options.Enabled` em `RemoteSessionsController.cs:116-117`).
- **Retorna 0 linhas** → o banco nega. Use Q2 e Q3 para saber qual condição caiu.

---

### Q5 — Outras roles de escopo Global que concedam `Agents:Execute`

Útil para saber se o usuário depende de uma única concessão (ponto único de falha).

```sql
SELECT r.name AS role_name, r.is_active AS role_active,
       ugr.scope_level, ugr.scope_id, g.name AS group_name, g.is_active AS group_active
FROM roles r
JOIN role_permissions rp ON rp.role_id = r.id
JOIN permissions      p  ON p.id = rp.permission_id
LEFT JOIN user_group_roles ugr ON ugr.role_id = r.id
LEFT JOIN user_groups      g   ON g.id = ugr.group_id
WHERE p.resource_type = 'Agents'
  AND p.action_type   = 'Execute'
  AND r.is_active     = true
ORDER BY r.name;
```

---

### Q6 — Estado global das roles (detecta desativação em massa)

```sql
SELECT name, type, is_system, is_active, created_at, updated_at
FROM roles
ORDER BY is_active, name;
```

Qualquer role `is_system = true` com `is_active = false` é anomalia: as roles de sistema
têm permissões fixadas por migration e não deveriam ser desativadas. O `updated_at` ajuda a
cruzar com a janela dos "ajustes".

---

### Q7 — Inventário de quem tem a concessão global

```sql
SELECT DISTINCT u.login, u.is_active AS user_active, r.name AS role_name,
       g.name AS group_name, ugr.scope_level, ugr.scope_id
FROM permissions p
JOIN role_permissions rp ON rp.permission_id = p.id
JOIN roles            r  ON r.id = rp.role_id
JOIN user_group_roles ugr ON ugr.role_id = r.id
JOIN user_groups      g  ON g.id = ugr.group_id
JOIN user_group_memberships ugm ON ugm.group_id = g.id
JOIN users            u  ON u.id = ugm.user_id
WHERE p.resource_type = 'Agents'
  AND p.action_type   = 'Execute'
  AND ugr.scope_level = 'Global'
  AND r.is_active     = true
  AND g.is_active     = true
  AND u.is_active     = true
ORDER BY u.login;
```

---

### Q8 — Auditoria temporal: o que mudou recentemente

Não existe tabela de histórico de RBAC (não há `*_audit` para `user_group_roles`), então a
evidência vem dos carimbos de tempo:

```sql
SELECT 'role' AS origem, r.name AS objeto, r.is_active AS ativo, r.updated_at AS mudou_em
FROM roles r
WHERE r.updated_at > now() - interval '30 days'
UNION ALL
SELECT 'grupo', g.name, g.is_active, g.updated_at
FROM user_groups g
WHERE g.updated_at > now() - interval '30 days'
UNION ALL
SELECT 'atribuicao', r.name || ' @ ' || ugr.scope_level, true, ugr.assigned_at
FROM user_group_roles ugr JOIN roles r ON r.id = ugr.role_id
WHERE ugr.assigned_at > now() - interval '30 days'
ORDER BY mudou_em DESC;
```

Em paralelo, checar a trilha de autenticação `auth_audit_logs` (criada na
`M117_CreateAuthAuditLogs.cs`) para separar 403 (autorização — este relatório) de 401
(autenticação — outra investigação).

---

## 6. Roteiro de diagnóstico (ordem recomendada)

1. **Q1** — a conta está ativa? Se não, pare aqui.
2. **Q2** — grupo, role e **escopo**. É aqui que a causa aparece na maioria dos casos.
3. **Q3** — a permissão `Agents:Execute` ainda existe na role?
4. **Q4** — o teste definitivo do banco.
5. **Se Q4 passa e o acesso continua negado:** invalidar o Redis (§4) — `DEL` do prefixo
   `perm:user:` — ou aguardar os **5 minutos** de TTL, e testar de novo. **Não é preciso
   reiniciar a API**: o `_userRoleCache` é scoped e já morreu com a unidade de trabalho
   anterior. Sempre descartar a hipótese de cache antes de reabrir o banco.
6. **Se o escopo caiu para Client/Site:** a correção é restaurar a atribuição para
   `scope_level='Global', scope_id=NULL` em `user_group_roles`, via
   `AssignRoleToGroupCommandHandler` (`UserGroupHandlers.cs:285-367`), que já invalida o
   cache em `:356`. Atenção: `RoleGrantScopePolicy.CanGrant` (`:324`) exige que quem
   concede tenha escopo compatível — um admin de cliente **não** consegue reconceder Global.

### Ressalvas

- As queries refletem o modelo EF conferido em `DiscoveryDbContext.Identity.cs`. Se o banco
  divergir (ex.: migration M195 não aplicada), `roles.is_active` pode não existir.
- Este relatório cobre **autorização**. Um 401 tem causa distinta (token/sessão) e não é
  explicado por este documento.
- Nenhum dado de produção foi consultado: as queries foram escritas a partir do esquema e
  precisam ser executadas pelo operador.
