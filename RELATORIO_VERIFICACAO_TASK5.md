# RELATÓRIO DE VERIFICAÇÃO INDEPENDENTE — task-5

**Verificador:** teammate `verifier` | **Tarefa:** task-5 (rev 2, claimed) | **Estado verificado:** congelado, nenhum escritor de produção ativo
**Workspace:** C:\Projetos\RMM_Discovery | **Data:** 2026-11-12

Veredito geral: **APROVADO COM RESSALVAS NÃO BLOQUEANTES.** Nenhum defeito bloqueante em aberto no estado congelado. Os 2 defeitos bloqueantes encontrados durante a entrega (cache de 30s e ciclo de DI) foram corrigidos e eu reconfirmei a correção no estado final. Todos os números relatados pelos autores foram **reproduzidos independentemente**.

---

## 1. Comandos executados e saída literal

### 1.1 Build (DiscoveryRMM_API)

```
$ dotnet build Discovery.slnx
...
Compilação com êxito.
    6 Aviso(s)
    0 Erro(s)
```

**Veredito: OK** — 0 erros e 6 avisos NU1902/NU1903 (vulnerabilidades de pacote, preexistentes). Reproduz o baseline exatamente.

Nota metodológica: um `dotnet build` **incremental** após eu adicionar meu arquivo de teste mostrou 34 avisos. Investiguei: são avisos **CS8602/CS8601/CS8604/CS8625/CS9113 preexistentes** em arquivos alheios à entrega (`HardwareInventoryParser.cs`, `UserHandlers.cs`, `ReportDatasetQueryService.cs`, `TicketRunbookTests.cs`, etc.) que só aparecem em rebuild completo (`--no-incremental`). Zerei a dúvida: **nenhum aviso** foi atribuído aos arquivos da entrega nem ao meu arquivo de teste.

### 1.2 Suíte completa (filtro do projeto)

```
$ dotnet test Discovery.slnx --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E"
Aprovado!  – Com falha:     0, Aprovado:  2914, Ignorado:     0, Total:  2914, Duração: 51 s
```

**Veredito: OK** — 0 falhas. Composição: **2869** baseline + **25** `McpToolRateLimitTests` + **3** `McpToolDependencyGraphTests` + **17** `McpToolRateLimitAuditTests` (meus) = **2914**. O número 2897 relatado pelos autores foi reproduzido por mim **antes** de eu adicionar meus 17 testes; após, 2914. Nenhum teste pré-existente foi removido ou silenciado.

### 1.3 Frontend (DiscoveryRMM_Site)

```
$ npm test
 Test Files  208 passed (208)
      Tests  1816 passed (1816)

$ npx tsc --noEmit
TSC_EXIT=0 (limpo)
```

**Veredito: OK** — 208 arquivos / 1816 testes, exatamente o relatado; `tsc` limpo.

---

## 2. Veredito por item de aceite

| # | Item | Veredito | Evidência |
|---|------|----------|-----------|
| a | Teto 20 + tool 10 ⇒ 10; teto 5 + tool 10 ⇒ 5 | **OK** | `Executor_GlobalCeiling20_Tool10_BlocksTheEleventhCall`, `Executor_GlobalCeiling5_Tool10_BlocksTheSixthCall` (17/17 verdes) |
| b | "Sem teto" ⇒ limite da TOOL, não 20 (sentinela 0) | **OK** | `Executor_WithoutConfigurationResolver_UsesTheToolLimit_NotTheDefault20` |
| c | Tool sem política continua SEM limite | **OK** | `Executor_ToolWithoutPolicy_IsNotRateLimited` (erro é "não autorizada", nunca rate limit) |
| d | Ordem: recusa não consome cota / efetivo desde a 1ª chamada | **OK** | `Executor_AppliesTheCeilingFromTheVeryFirstCall`; código: `McpToolExecutor.cs:250-263` resolve **antes** de `:266` incrementar |
| e | Coluna `mcp_tool_calls_per_minute` nas 3 tabelas + nulabilidade | **OK** | `MigrationColumnNames_MatchTheRelationalModel` (fonte da M287 × modelo EF real, Npgsql+pgvector) |
| f | Nada vaza para `Security:RateLimiting:*` | **OK** | `HttpRateLimiting_IsNotWiredToTheMcpToolCeiling`, `AppSettingsHttpRateLimits_DoNotContainTheMcpToolCeiling` |
| g | Guard de DI tem DENTES | **OK (provado por mutação)** | ver §3 |
| h | Payload do agente aditivo | **OK** | `git diff` do handler: apenas `+ ["mcpToolCallsPerMinute"]`, nenhuma chave removida/renomeada |

---

## 3. Item (g): o guard de DI tem dentes? — PROVA POR MUTAÇÃO

Passar não prova nada; um guard verde pode estar verde por acaso. Executei um **teste de mutação controlado**:

1. Fiz backup byte a byte de `McpToolExecutor.cs` (56499 bytes).
2. Reintroduzi **o ciclo real de produção**: adicionei `IMcpToolGovernance? governanceProbe = null` ao construtor do executor (o mesmo ciclo `IMcpToolExecutor -> IMcpToolGovernance -> IMcpToolExecutor`).
3. Rodei o guard: `dotnet test --filter "FullyQualifiedName~McpToolDependencyGraphTests"`
   → **exit=1, "Com falha! – Com falha: 2, Aprovado: 1, Total: 3"**. O guard **reprovou**.
4. **Restaurei** o arquivo e confirmei byte-identidade (`RESTAURADO byte-identico? True`) e que `governanceProbe` não existe mais no fonte nem no `git diff`.

Conclusão: o guard tem dentes reais contra a reintrodução do ciclo, e não apenas contra o fake sintético do autor. Ao final, o arquivo de produção estava íntegro.

> Primeira tentativa de mutação (substituir `IConfigurationResolver` por `IMcpToolGovernance`) **quebrou a compilação** (CS1061), o que não é um teste válido do guard. Restaurei imediatamente e refiz a mutação de forma precisa e compilável. Registro a tentativa falha por transparência: a prova válida é a segunda.

---

## 4. Defeitos encontrados durante a entrega (ambos corrigidos e reverificados)

### DEF-1 (BLOQUEANTE, corrigido) — cache de 30s não invalidado ao salvar configuração
- **Achado por mim** na revisão do código intermediário: `McpToolGovernance` mantinha `EffectiveCeilingCache` estático com TTL de 30s, **sem nenhum ponto de invalidação**. `grep` do símbolo retornava apenas as 3 linhas da própria governança; `ConfigurationService.cs:131-417` chamava `_resolver.ClearCache()` em 12 pontos de escrita e **nenhum** limpava esse cache.
- **Impacto:** baixar o teto de 60 para 5 levava até 30s para valer — um kill switch de rate limit que não aplica de imediato.
- **Correção (melhor que a combinada):** o cache estático foi **removido pela raiz**. `grep` de `EffectiveCeilingCache`/`ClearEffectiveCeilingCache`/`CeilingCacheSeconds` em `DiscoveryRMM_API/src` hoje retorna **zero ocorrências**. O único cache no caminho passou a ser o do `ConfigurationResolver` (Redis, 5 min), que o `ConfigurationService` **já** invalida nos 12 pontos de escrita `_resolver.ClearCache()`. Salvar a configuração passa a aplicar o novo teto imediatamente, sem acoplamento novo.
- **Reverificado:** `ResolveMaxCallsPerMinuteAsync_ReflectsANewlySavedToolLimitImmediately` e `..._AppliesTheCeilingToANewlySavedLimitImmediately` (task-4) verdes.

### DEF-2 (BLOQUEANTE, corrigido) — ciclo de DI que atravessava build e suíte verdes
- **Achado pela auto-auditoria da api-core** (eu confirmei o encadeamento): `McpToolExecutor(..., IMcpToolGovernance? governance = null)` + `McpToolGovernance(IMcpToolPolicyRepository, IMcpToolExecutor)` ⇒ ciclo `IMcpToolExecutor -> IMcpToolGovernance -> IMcpToolExecutor`.
- **Por que passou pelo CI:** o Microsoft DI só usa o valor default quando o serviço **não** está registrado; `IMcpToolGovernance` **está** registrado (`Program.cs:228`). E `Program.cs` **não** usa `ValidateOnBuild` (confirmei: `grep 'ValidateOnBuild|ValidateScopes|BuildServiceProvider'` em `Discovery.Api` = **zero**; `Program.cs:67` usa `WebApplication.CreateBuilder`). O grafo só é materializado no **primeiro request**, então o erro apareceria em runtime com CI 100% verde. `IMcpToolExecutor` é dependência do chat inteiro — quebraria a API em produção.
- **Correção (Opção B) reverificada por mim no estado final:**
  - `McpToolExecutor.cs:111` → `private readonly IConfigurationResolver? _configurationResolver;` (`:125` último parâmetro). `grep` de `IMcpToolGovernance` no arquivo retorna **apenas comentários** (`:100/103/107`), zero uso de código.
  - `McpToolGovernance.cs:29` mantém `IConfigurationResolver?` opcional e `:27` o `IMcpToolExecutor` (para o catálogo).
  - **Grafo acíclico confirmado até as folhas:** `ConfigurationResolver` (`:21-44`) depende só de `IServerConfigurationRepository`, `IClientConfigurationRepository`, `ISiteConfigurationRepository`, `ISiteRepository`, `IRedisService`, `ISecretProtector`; e os repositórios são folha (`ClientConfigurationRepository.cs:13`, `SiteConfigurationRepository.cs:13`, `SiteRepository.cs:13` recebem só `DiscoveryDbContext`; `ServerConfigurationRepository.cs:21` recebe DbContext+Redis+IConfiguration). Nenhuma aresta de volta ao executor.
  - `ConfigurationResolver` **não** referencia `IMcpToolExecutor` (`grep` = zero) — confirmado por `ConfigurationResolver_DoesNotDependOnTheExecutor`.

---

## 5. Pontos únicos e consistência (verificados)

- **Ponto único do cálculo do efetivo: OK.** `McpToolRateLimitCatalog.Effective` (`:58-59`) é o único lugar com a regra MIN; os dois chamadores são `McpToolGovernance.cs:205` e `McpToolExecutor.cs:253-254`. `grep` de `Math.Min` no repo: as únicas ocorrências ligadas a este rate limit são o próprio `Effective` e `McpToolsController.cs:49` (clamp de **entrada** da tela, contrato diferente). Meu teste `GovernanceAndCatalogHelper_AgreeOnTheEffectiveLimit` confronta o helper com o resultado real da governança para 6 valores.
- **Contrato do sentinela 0 (intencional, ratificado com o Lead):** `McpToolGovernance.cs:264-270` **não** normaliza o teto: `return ceiling <= 0 ? 0 : ceiling;`. A sanitização 1..600 vive em `ConfigurationResolver.ResolveMcpToolCallsPerMinute` (`:349-350`). Logo, config fora de 1..600 ⇒ 20 (nunca desliga); `ceiling <= 0` só ocorre no caminho defensivo (resolvedor ausente/falha) e significa "sem piso global". Meu teste `GlobalCeiling_OutOfRangeInConfiguration_FallsBackToTwentyAndNeverDisables` cobre `0,-3,9999,7,600`.
- **Fail-open agora é visível:** `McpToolGovernance.cs:252-261` loga `LogWarning` com escopo site/client antes de devolver 0 — a degradação silenciosa foi eliminada (`McpToolExecutor.cs:256-263` também loga).
- **`ResolveAgentScopeAsync` real (não o default de identidade):** `AgentScopeOnly_ResolvesTheAgentsSite_AndAppliesItsCeiling` usa o repositório real contra InMemory com `Site`+`Agent` e prova que um escopo só com `agentId` resolve o site e o cliente — sem isso o teto do site seria ignorado para tools executadas em nome do agente.
- **`CheckRange` × `Nullable<int>`: verificado-OK (meu falso positivo, retratado).** Eu havia levantado que `prop.GetValue(obj) is int` (`ConfigurationService.cs:669`) não casaria com `int?` boxed. **Estava errado:** `Nullable<T>` boxeia como `T` quando tem valor. Provei empiricamente com um probe isolado (compilado com `csc` direto, fora do repo): `Nullable_601_is_int=True`, `Nullable_null_is_int=False`, `Nullable_601_boxed_type=Int32`. Logo a validação cobre servidor (`int`) e cliente/site (`int?` com valor); `null` não é validado = herda, que é o correto. **Retirei o achado.**
- **Migration M287: OK.** `[Migration(20270120_287)]` > M286 (`20270118_286`); `Version` crescente e contiguidade passam em `MigrationIntegrityTests`. Nome `mcp_tool_calls_per_minute` nas 3 tabelas, com `NOT NULL DEFAULT 20` no servidor e `NULL` em cliente/site; `Down()` espelha o `Up()`. Os autores ainda adicionaram mapeamento EF **explícito** (`DiscoveryDbContext.Configurations.cs`, 3 linhas `HasColumnName("mcp_tool_calls_per_minute")`) — mais robusto que depender só do `ApplySnakeCaseFallback` (`DiscoveryDbContext.cs:263-287`), que também daria o nome certo.
- **Auto-registro: OK.** `McpToolPolicyRepository.cs:200` usa `McpToolRateLimitCatalog.DefaultCallsPerMinute`; o antigo ternário `Agent ? 10 : 5` não existe mais.
- **Frontend: OK.** Campo nas 3 telas (`configurationEditors.ts:559` servidor com `defaultValue: "20"`; `:886` cliente; `:1102` site), range `[1,600]` (`:1352`), `ConfigurationFieldEditor.tsx:98` `{min:1,max:600}`, `types.ts:1937/2007/2043`. Cliente/site **sem** `defaultValue` (não gravam override silencioso de 20). O `fieldDefault` (`:1697-1715`) preserva `"0"` para os demais campos `number`.

---

## 6. Lista de defeitos

| ID | Severidade | Descrição | Estado |
|----|-----------|-----------|--------|
| DEF-1 | **Bloqueante** | Cache estático de 30s do teto global sem invalidação ao salvar configuração | **Corrigido** (removido pela raiz); reverificado |
| DEF-2 | **Bloqueante** | Ciclo de DI `IMcpToolExecutor -> IMcpToolGovernance -> IMcpToolExecutor`, invisível ao CI | **Corrigido** (Opção B); reverificado por leitura do grafo + teste de mutação |
| DEF-3 | **Baixa (nit)** | Re-sanitização redundante: `McpToolGovernance.cs:237/243/249` reaplicam `ResolveMcpToolCallsPerMinute` sobre um valor que o resolver já sanitizou (`McpToolExecutor.cs:184/190` não reaplicam) | **Aceito como nit** — `Normalize` é idempotente, resultado idêntico; sem pedido de refactor |
| DEF-4 | **Baixa (processo)** | Falta de `ValidateOnBuild`/`ValidateScopes` permite que ciclos de DI escapem do CI | **Recomendação de produto** (não desta entrega) |

Nenhum defeito bloqueante em aberto.

---

## 7. Falsos positivos do meu próprio processo (transparência)

Registro porque um verificador que não erra é um verificador que não verifica:

1. **H#8 (`CheckRange` × `Nullable<int>`)** — levantado como defeito de validação em cliente/site; **refutado por probe empírico** e retratado (§5).
2. **Asserções de contador nos meus testes** — escrevi `counter.IncrementCalls == 11` sem que o executor de produção chamasse o contador do meu fake; a asserção seria falsa e faria o teste falhar por motivo errado. **Removi antes de rodar.**
3. **Dois testes meus falharam na 1ª execução** e ambos eram **bug do meu teste**, não da produção: (i) eu exercitava o sentinela 0 com um resolvedor **presente** (que devolve o default 20) em vez de **ausente**; (ii) meu resolvedor fake tinha construtor `(int)`, que o container não sabe construir. Corrigi os dois; a produção não foi tocada.
4. **Primeira mutação de DI inválida** — quebrou a compilação em vez de exercitar o guard; refeita de forma precisa (§3).

---

## 8. Lição para o processo (explícita, a pedido)

> **Build verde + suíte verde NÃO provam grafo de injeção de dependência.**

Motivo concreto neste repositório: com `ValidateOnBuild` desligado (`Program.cs:67`, `WebApplication.CreateBuilder`), o container só materializa o grafo no **primeiro request**. Um ciclo como `IMcpToolExecutor -> IMcpToolGovernance -> IMcpToolExecutor` **compila**, passa por todos os testes unitários (que constroem os objetos à mão, sem container) e explode apenas em runtime (`InvalidOperationException: A circular dependency was detected`). Um parâmetro com valor default **não** protege: o container só usa o default quando o serviço **não** está registrado.

**Recomendação de produto (fora do escopo desta entrega):** ligar `ValidateOnBuild = true` e `ValidateScopes = true` no ambiente de Development e/ou adicionar um teste que construa o provider de produção com essas opções. É a única forma de o CI capturar automaticamente essa classe de defeito. O guard em `McpToolDependencyGraphTests` já faz isso no escopo da governança de MCP tools.

---

## 9. Reprodutibilidade

Comandos, na ordem, em `C:\Projetos\RMM_Discovery\DiscoveryRMM_API`:
```
dotnet build Discovery.slnx
dotnet test Discovery.slnx --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E"
```
Em `C:\Projetos\RMM_Discovery\DiscoveryRMM_Site`: `npm test` e `npx tsc --noEmit`.

**Arquivo exclusivo do verificador (única escrita além deste relatório):**
`DiscoveryRMM_API/src/Discovery.Tests/McpToolRateLimitAuditTests.cs` — 17 testes, todos verdes, zero avisos do compilador atribuídos a ele. Nenhum arquivo de produção foi alterado por mim; a mutação temporária de §3 foi revertida byte a byte (comprovado por `git diff`).
