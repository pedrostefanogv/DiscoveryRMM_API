# Auditoria de Migrations — Defeitos da Classe M279 (SQL quebrado)

**Auditor:** sql-auditor (task-2)
**Data:** 2026-11-07
**Escopo:** `DiscoveryRMM_API/src/Discovery.Migrations/Migrations/*.cs` — **277 arquivos**
**Modo:** SOMENTE LEITURA no repositório. Nenhuma migration foi alterada.
**Branch:** `dev` · HEAD `aac4e82`

---

## 1. Resumo executivo

| Métrica | Valor |
|---|---|
| Arquivos de migration auditados | **277** |
| Arquivos com `Execute.Sql` real | **86** |
| Literais SQL extraídos e analisados | **220** |
| Chamadas com SQL montado em runtime | **2** |
| **CONFIRMADOS quebrados (classe M279)** | **0** |
| CONFIRMADOS de qualquer outra classe de sintaxe | **0** |
| **SUSPEITOS** (revisão humana recomendada) | **1** |
| Falsos positivos do próprio detector (descartados) | 3 |

**Conclusão: NÃO há nenhuma outra migration com o defeito da classe M279.**

O defeito de M279 era: aspas simples duplicadas inválidas (`''null''` → `''` + `null` + `''`)
e uma vírgula espúria antes de `AND`. **A varredura completa das 220 literais SQL não
encontrou nenhuma outra ocorrência dessa classe.** A única ressalva metodológica está
na §5: **não houve validação contra um PostgreSQL real**, por indisponibilidade.

> **Estado do M279 na árvore de trabalho:** o arquivo está **já corrigido**
> (`git status` mostra `M` em `M279_AddCanExecuteOnMachinesToDepartments.cs`).
> A versão quebrada está no **commit** `aac4e82`; a correção está apenas no working tree,
> **não commitada**. Isso é um achado operacional por si só (§4.4).

---

## 2. Método (e por que grep não bastaria)

Conforme `AGENTS.md`, o MCP de grafo foi usado primeiro
(`get_architecture`, `search_graph` — 34.970 nós / 169.712 arestas em `RMM-API`),
seguido de leitura direta e de scripts de verificação.

O ponto crítico: **a cadeia de escaping do C# não pode ser inferida por regex.**
Em `@"..."` (verbatim) **aspas simples não são especiais** — logo `''X''` chega
**literalmente** ao PostgreSQL. Já em `$"""..."""` (raw string) não há escape algum.
Confundir as duas formas gera tanto falso negativo quanto falso positivo.

Por isso foi implementado um pipeline em Python que:

1. **Extrai** o argumento de cada `Execute.Sql(`, tratando **6 formas** de literal C#:
   `@"..."`, `"..."`, `$@"..."`, `$"..."`, `$"""..."""`, `"""..."""`
   (com resolução de constantes `const string`, ex.: M242/M185 usam `Alter.Table(Table)`).
2. **Desfaz o escaping** de cada forma e produz o **SQL efetivo** entregue ao servidor.
   Interpolações `{expr}` são substituídas por um placeholder balanceado (com *brace
   matching* ciente de aspas), para não corromper a análise léxica.
3. **Lexa** o SQL com um lexer compatível com PostgreSQL, determinando os limites de
   string/identificador **como o servidor determina** — não por heurística de linha.
4. Aplica as checagens: aspas duplicadas residuais, vírgula antes de
   `AND/OR/WHERE/ON`/`)`, balanceamento de parênteses, identificador `""` vazio,
   e comparação jsonb sem cast.

**Controle positivo (validação do detector).** O detector foi executado contra a versão
**quebrada** de M279 (extraída de `git show` HEAD). Ele acusou **exatamente** os três
defeitos reais — vírgula espúria antes de `AND`, parênteses desbalanceados e sequência
de aspas duplicadas. Sem esse controle, "zero achados" seria indistinguível de
"detector quebrado".

---

## 3. O que foi PROVADO

### 3.1 Zero ocorrências da assinatura M279 nas 220 literais

A assinatura exata — token de aspas simples com número **par** ≥ 4 de aspas
(`''''` = duas strings vazias adjacentes), e vírgula imediatamente antes de
`AND/OR/WHERE/ON` — foi procurada e **não ocorre em nenhuma migration**.

Verificação independente por busca literal: a sequência `''''` (quatro aspas simples)
**não existe em nenhum dos 277 arquivos** da árvore de trabalho.

### 3.2 Todas as 16 ocorrências de `''` são legítimas

Somente 16 linhas em 9 arquivos contêm `''`, e **cada uma foi inspecionada
individualmente**. Todas são legítimas, em duas categorias:

| Arquivo:linha | Trecho | Por que é legítimo |
|---|---|---|
| [M236_SeedAgentContextMcpTools.cs:118](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M236_SeedAgentContextMcpTools.cs#L118) | `Replace("'", "''")` | Escape **correto** de aspa simples para injeção em SQL |
| [M274_SeedRemoteSessionPermissionAndPersonas.cs:114](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M274_SeedRemoteSessionPermissionAndPersonas.cs#L114) | `roleName.Replace("'", "''")` | idem (114, 115, 167) |
| [M278_SeedDepartmentTeamRoles.cs:104](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M278_SeedDepartmentTeamRoles.cs#L104) | `Replace("'", "''")` | idem (104, 105, 154, 155) |
| [M017_MoveSoftwareVersionToAgentInventory.cs:26](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M017_MoveSoftwareVersionToAgentInventory.cs#L26) | `inv.version = ''` | Comparação **com string vazia** — SQL válido |
| [M230_CreatePatchManagement.cs:27](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M230_CreatePatchManagement.cs#L27) | `DEFAULT ''` | Default de string vazia — válido (27, 129) |
| [M179_AddAiTicketAssignment.cs:248](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M179_AddAiTicketAssignment.cs#L248) | `coalesce(description, '')` | Argumento de `coalesce` — válido |
| [M224_ConsolidateSoftwareCatalogIdentity.cs:338](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M224_ConsolidateSoftwareCatalogIdentity.cs#L338) | `regexp_replace(..., '', 'g')` | Argumento de substituição — válido (338, 339) |
| [M225_AddNatsWebSocketExternalUrl.cs:17](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M225_AddNatsWebSocketExternalUrl.cs#L17) | `/// ... DEFAULT ''.` | **Comentário XML**, não SQL |

> **Por que a distinção importa:** `''` em SQL **é** uma string vazia válida. O erro de
> M279 não foi "ter `''`", foi ter `''` **onde deveria haver um literal com conteúdo**
> (`''null''` → virou `''` + `null` + `''`). A classe do defeito é *aspas duplicadas
> órfãs*, não *aspas duplicadas* em si.

### 3.3 Todas as referências de esquema citadas na task EXISTEM

Verificado por modelo de esquema construído a partir das próprias migrations
(142 tabelas modeladas, resolvendo `Alter.Table(Table)` via constantes):

| Referência citada na task | Situação | Evidência |
|---|---|---|
| `ticket_macros.department_id` | **EXISTE** | criada em [M158:20](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M158_CreateSupportProductivityTables.cs#L20) |
| `ticket_macros.is_active` | **EXISTE** | criada em [M158:24](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M158_CreateSupportProductivityTables.cs#L24) |
| `ticket_macros.runbook_steps_json` | **EXISTE** | criada em [M242:23](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M242_AddRunbookStepsToTicketMacros.cs#L23) como `jsonb` |
| `departments.can_execute_on_machines` | **EXISTE** | criada em [M279:31](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M279_AddCanExecuteOnMachinesToDepartments.cs#L31) |

Ou seja: **a correção aplicada em M279 está coerente com o esquema real** —
`runbook_steps_json` é de fato `jsonb`, então os casts `'null'::jsonb` e `'[]'::jsonb`
são os tipos corretos.

### 3.4 Cross-check de colunas: nenhuma coluna inexistente

Todas as referências qualificadas `alias.coluna` nas 220 literais foram confrontadas
com as 142 tabelas modeladas. **Nenhuma coluna inexistente foi encontrada.** As 5
ocorrências sinalizadas pelo script foram **descartadas como artefato do próprio
script**, e não como defeito:

| Sinalizado | Veredito | Motivo |
|---|---|---|
| `information_schema.columns` (M130) | **artefato** | Catálogo de sistema — fora do esquema da aplicação |
| `__interp__.*` (M185) | **artefato** | `{Table}` = constante `"p2p_agent_telemetry"`, resolvida em runtime; tabela existe |
| CTEs `ranked`/`ranked_active` (M082, M172) | **artefato** | Nomes de CTE (`WITH`), não tabelas físicas |

---

## 4. Achados

### 4.1 Tabela de achados

| Arquivo | Linha | Trecho | Classe | Veredito | Evidência |
|---|---|---|---|---|---|
| *nenhum* | — | — | aspas duplicadas classe M279 | — | **0 ocorrências** em 220 literais + 277 arquivos |
| *nenhum* | — | — | vírgula espúria antes de AND/OR/WHERE/ON | — | **0 ocorrências**; controle positivo confirma que o detector pega |
| *nenhum* | — | — | `""` / aspas desbalanceadas | — | **0 ocorrências** |
| *nenhum* | — | — | coluna/tabela inexistente | — | **0 ocorrências** (142 tabelas modeladas) |
| [M114_AddRetentionSettings.cs:16-20](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M114_AddRetentionSettings.cs#L16-L20) | 16, 20 | `.AsString(int.MaxValue)` + `SET retention_settings_json = '{...}'` | Tipo divergente do modelo EF | **SUSPEITO** | Ver §4.2 |

### 4.2 SUSPEITO — M114: coluna `text` mapeada como `jsonb` no EF

**O que foi observado (fato):**

- [M114:16](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M114_AddRetentionSettings.cs#L16) cria a coluna como `.AsString(int.MaxValue)` → **`text`**.
- [M114:20](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M114_AddRetentionSettings.cs#L20) grava um **literal de texto** JSON — correto para `text`.
- [DiscoveryDbContext.Configurations.cs:48](DiscoveryRMM_API/src/Discovery.Api/Data/Entities/DiscoveryDbContext.Configurations.cs#L48) mapeia a mesma coluna como `.HasColumnType("jsonb")`.
- **Nenhuma migration posterior converte a coluna para `jsonb`** (verificado: só 6 referências a `retention_settings` em todo o repo).

**Contraprova (por que pode ser inofensivo) — e por isso NÃO é CONFIRMADO:**

1. A propriedade CLR é `public string RetentionSettingsJson { get; set; }`
   ([ServerConfiguration.cs:142](DiscoveryRMM_API/src/Discovery.Api/Data/Entities/ServerConfiguration.cs#L142)).
   Com tipo CLR `string`, o EF lê e escreve como texto; `HasColumnType("jsonb")`
   só se materializa em **DDL gerado**, que aqui não é usado (o esquema vem de migrations).
2. O campo é lido com `ParseRetentionSettings(...)` / `TryDeserializeJson(...)`,
   que desserializam **strings** — compatível com `text`.
3. O valor gravado é JSON válido, então `::jsonb` também funcionaria.
4. Este tipo de divergência (coluna `text` + mapeamento `jsonb`) é **recorrente** na
   base (`AsString(int.MaxValue)` aparece 25×), sugerindo convenção antiga e não defeito pontual.

**Por que ainda assim é reportado:** é uma inconsistência real entre migration e modelo.
**Não é** um erro 42601, **não** quebra sintaxe, e **não** foi provado que quebra execução.
Exige uma migration de `ALTER COLUMN ... TYPE jsonb` **apenas se** o DDL do EF for usado
como fonte de verdade. **Recomendação: investigar, não alterar às cegas.**

### 4.3 SQL dinâmico montado em runtime — verificado manualmente

2 de 222 chamadas não são literais estáticos. Foram **reproduzidas e validadas**:

| Local | Construção | Veredito |
|---|---|---|
| [M229:59](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M229_AddDepartmentInheritanceOverrides.cs#L59) → `BuildUpdateSql` [:146](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M229_AddDepartmentInheritanceOverrides.cs#L146) | `string.Join(",\n", colunas → "x = y")` | **Limpo** — reproduzido: `SET a_override = a, b_override = b, ...`; sem vírgula espúria |
| [M235:59](DiscoveryRMM_API/src/Discovery.Migrations/Migrations/M235_AddRemoteSessionConsentColumns.cs#L59) | `ALTER TABLE ... ADD COLUMN` + default condicional | **Limpo** — 5 variantes reproduzidas, todas válidas |

### 4.4 Achado operacional (fora do escopo de sintaxe, mas relevante)

A correção de M279 está **apenas no working tree**, não commitada (`git status: M`).
Enquanto não for commitada, a versão quebrada continua sendo a verdade do branch `dev`
e qualquer deploy a partir do repositório reproduz a queda do 42601.

---

## 5. O que NÃO foi possível validar (limite honesto)

**Não houve validação de sintaxe contra PostgreSQL real.** Isso é um limite material
deste relatório e não deve ser lido como "sintaxe provadamente válida".

- `psql` **não está instalado**; não há `C:\Program Files\PostgreSQL`; nenhum serviço
  `postgres`; nenhuma porta 5432/5433 em escuta.
- O host configurado em `appsettings` (`192.168.1.21:5432`) **não respondeu**
  (`TcpTestSucceeded: False`).
- `docker` não está disponível para subir um contêiner efêmero.
- Credenciais (`${DB_PASSWORD}`) não resolvidas — e não seria apropriado tentar.

**O que foi feito em substituição:** um lexer compatível com PostgreSQL escrito para esta
auditoria, **calibrado contra um caso real conhecido-quebrado** (controle positivo, §2).
Isso eleva bastante a confiança nas checagens léxicas e estruturais, mas **não equivale**
ao parser do servidor.

**Cobertura desta validação:**
- Forte: aspas, delimitadores, vírgulas, parênteses, balanceamento — checagens **léxicas**
  e **estruturais**, exatamente onde mora o defeito M279.
- **Não coberta**: validade semântica (funções/operadores inexistentes, resolução de
  tipos, dependências de extensão como `pg_trgm`, compatibilidade de versão do PG).
  Um `42601` dessa natureza **não seria detectado** por este método.

**Recomendação de validação definitiva:** aplicar as migrations num banco PostgreSQL
descartável (`MigrateUp` em base vazia) em CI, que é o único teste que prova sintaxe.

---

## 6. Reprodução

Artefatos do auditor (fora do repositório, para respeitar SOMENTE LEITURA):

```text
%TEMP%\m279audit\
  extract.py       extrai Execute.Sql das 277 migrations, 6 formas de literal C#
  detect.py        lexer PostgreSQL + checagens estruturais
  schema_model.py  constrói modelo de esquema (142 tabelas) das migrations
  schema_check.py  cross-check de alias.coluna contra o modelo
  control.py       CONTROLE POSITIVO contra o M279 quebrado (HEAD)
  run_all.py       executa o pipeline completo
```

Resultado do detector sobre a árvore atual: `confirmed: 0`, `suspect: 1` (M114).
Resultado sobre o M279 quebrado: `CONFIRMADO` vírgula espúria + parênteses + aspas duplicadas.

---

## 7. Recomendações

1. **Nenhuma alteração** nas 276 migrations restantes — não há defeito da classe M279.
2. **Commitar a correção de M279** (§4.4): é o risco ativo mais concreto hoje.
3. **Adicionar validação real em CI**: `MigrateUp` em PostgreSQL descartável. Um teste com
   `grep` não pega esta classe de defeito; foi exatamente por isso que M279 passou.
4. **Investigar M114** (§4.2) antes de qualquer mudança — pode ser inofensivo.
5. Se quiser prevenção estrutural: um teste que lexe o SQL das migrations e falhe em
   `''` órfão / vírgula antes de `AND` tornaria a classe M279 impossível de repetir.
