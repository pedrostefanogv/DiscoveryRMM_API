# RELATÓRIO DE VERIFICAÇÃO INDEPENDENTE — task-22 (tolerância de cerca A2UI)

**Verificador:** teammate `verifier` (task-22, rev 2) | **Workspace:** C:\Projetos\RMM_Discovery
**Escopo:** DiscoveryRMM_API (.NET) | **Arquivo alvo:** `src/Discovery.Infrastructure/Services/Ai/AiChatA2uiExtractor.cs`

## Veredito

**APROVADO — nenhum defeito bloqueante.** A tolerância conserta o **caso real de produção** sem abrir a porta para falso positivo. Reproduzi o bug a partir do conteúdo **real do banco**, rodei as duas mutações (5 e 11 reprovações) e confirmei o **defeito ampliado pela própria correção**, que foi corrigido na mesma onda.

| Verificação | Resultado |
|---|---|
| `dotnet build Discovery.slnx` | **0 Erros**, 6 Avisos |
| `dotnet test` (suite completa) | **0 falhas, 2998 aprovados** |
| Mutação M1 (remover a trava da condição 2) | **5 testes REPROVARAM** |
| Mutação M2 (desligar a tolerância) | **11 testes REPROVARAM** |
| Restauro | **SHA256 idêntico** (`949B9FDF…28ADF`) + nenhum marcador no diff |
| Falhas conhecidas da suíte | **nenhuma** (nem os 2 flakes apareceram) |

Confirmei a contagem **2998** por conta própria: o testador primeiro atribuiu a diferença a testes do dono da task-20 (informação **incorreta**, que ele mesmo retificou). A causa real é a **expansão do `[Values(8)]`**: a fixture tem **21 métodos `[Test]`**, e um deles expande para 8 casos → `21 − 1 + 8 = 26`. Rodei a fixture isolada e ela reporta **26**. `2972 + 26 = 2998`, fecha exatamente.

---

## 1. CASO REAL REPRODUZIDO (conteúdo extraído do banco, não inventado)

Fonte: `C:\ProgramData\Discovery\logs\chat.db` (438.272 bytes, tabela `chat_entries`, 1025 linhas).

Busquei a string `="a2ui"` em **todas as colunas**: **1 único acerto** → `id=1025`, coluna `assistant`, 2750 bytes, `ts=2026-11-12T23:19:49.919Z`, `type=multi_round_done`.

```
primeiros bytes (repr): '="a2ui"\n{"version":"v0.9","createSurface":...
contagens: ``` = 1 | ```a2ui = 0 | ="a2ui" = 1
```

### 1.1 Confirmei as duas premissas do brief

| Premissa | Verificação |
|---|---|
| **Conteúdo íntegro** | Linha 1 = JSON válido, `version=v0.9`, `createSurface`, `surfaceId=health_check`. Linha 2 = JSON válido, `updateComponents`, **17 componentes**, **root presente**. |
| **Bug confirmado** | Reimplementei a regex estrita e rodei sobre o conteúdo real → **0 blocos reconhecidos, 0 mensagens**, e o `CleanContent` **contém** `createSurface`. O JSON cru ia para a tela. |

### 1.2 Estrutura do turno (determina o desenho)

```
[0] ="a2ui"                    <- cerca quebrada
[1] createSurface (JSON)
[2] updateComponents (JSON, 17 componentes, root)
[3] ```                       <- FECHAMENTO (única cerca que sobrou)
[4..13] PROSA LEGÍTIMA
```

O fechamento está **antes** da prosa — bom (não engole texto), mas exige que o bloco **pare no fechamento** e não continue consumindo.

---

## 2. DEFEITO AMPLIADO PELA PRÓPRIA CORREÇÃO (o achado central)

**Encontrado pelo testador, confirmado por mim com prova executável, corrigido na mesma onda.**

`IsValidA2uiMessage` fazia `JsonDocument.Parse` seguido de `TryGetProperty`. O `Parse` **aceita raiz não-objeto** (`[1,2,3]`, `42`, string, `true`, `null` são JSON válido), mas `TryGetProperty` nelas lança **InvalidOperationException** — que **não** é `JsonException`, então o `catch (JsonException)` **não captura** e a exceção **escapava de `Extract()`**.

**Prova empírica** (probe C# isolado, compilado com `csc` fora do repo):

| Entrada | Resultado |
|---|---|
| `[1,2,3]` | **InvalidOperationException** — escapa do catch(JsonException) |
| `42` | **InvalidOperationException** — escapa |
| string | **InvalidOperationException** — escapa |
| `true` | **InvalidOperationException** — escapa |
| `null` | **InvalidOperationException** — escapa |
| `[{"foo":1}]` | **InvalidOperationException** — escapa |

**Por que a correção AMPLIOU isso:** antes, `IsValidA2uiMessage` só era chamada em `ParseBlock`, sobre linhas **dentro de um bloco já reconhecido** (entrada controlada). Com a **condição 2**, passou a receber a **linha seguinte de qualquer cerca quebrada** — JSON arbitrário vindo do modelo. Cenário de quebra: cerca quebrada + `[{"foo":1}]` ⇒ `Extract` **lança** em vez de cair no fail-safe.

> **A tolerância deveria ser fail-safe e virou um caminho de exceção.** É a classe exata de regressão que uma correção de robustez não pode introduzir — e só era alcançável **por causa** da nova chamada.

### 2.1 Correção verificada no freeze (as DUAS guardas)

| Local | Linha | Estado |
|---|---|---|
| `IsValidA2uiMessage` | **528** | guarda `ValueKind != JsonValueKind.Object` **antes** do `TryGetProperty` |
| `Classify` | **349** | mesma guarda (padrão idêntico, risco latente) |

Estavam **ambas em disco** no freeze — reconfirmei por grep e por leitura. A do `Classify` tem o comentário correto: os 3 call sites dependem de uma **invariante não expressa no tipo**, e sem a guarda qualquer refatoração que chame `Classify` antes da validação reintroduz o bug.

**Reclassificação:** eu havia registrado o `Classify` como *NIT de risco latente*. Como o dono aplicou a guarda, registro como **CORRIGIDO NA MESMA ONDA**.

### 2.2 Verificação do predicado (pedido do Lead)

- **Por leitura:** a guarda na linha 528 precede o `TryGetProperty`. OK
- **Por cobertura:** o testador cobre nos dois níveis — `IsToleratedA2uiOpenLine_ReturnsFalseForNonObjectJsonRoots_WithoutThrowing` (8 raízes: `[1,2,3]`, `42`, string, `true`, `false`, `null`, `[]`, `{}`) e `BrokenFence_FollowedByJsonArray_DoesNotThrowAndLeavesTheTextIntact`. OK
- **Por comportamento:** rodei os 4 testes do predicado/array/bloco oficial → **4/4 verdes**, sem exceção. OK

**Nota:** o `[{"foo":1}]` **permanecer** no texto visível é o **fail-safe CORRETO** — a linha não é engolida. **Não é bug** (conforme o Lead definiu).

---

## 3. AS DUAS MUTAÇÕES

Backup byte a byte do arquivo congelado antes de qualquer alteração. Cada mutação: aplicada → suíte rodada com saída em **ARQUIVO** → restaurada → identidade provada.

### M1 — remover a trava da condição 2

`IsToleratedA2uiOpenLine` → `return true;`

```
Com falha! – Com falha: 5, Aprovado: 2993, Total: 2998

PlausibleFence_AtEndOfContent_DoesNotOpenABlock
PlausibleFence_FollowedByBlankLine_DoesNotOpenABlock
PlausibleFence_FollowedByNonA2uiJson_DoesNotOpenABlock
RejectedFences_AlwaysLeaveTheContentByteIdentical
IsToleratedA2uiOpenLine_RequiresAValidA2uiLineRightAfter
```

**Tem dentes.** As 5 reprovações cobrem os falsos positivos **e** o guard byte-a-byte de "não engolir texto". **Bate exatamente com a contagem independente do testador (5).**

**Restauro:** byte-idêntico OK · `MUTACAO1` ausente OK · **SHA256 = `949B9FDF528847D5E0F577F633523328B6843668106D2589D9886AE5B9828ADF`** (idêntico ao freeze).

### M2 — desligar a tolerância

`IsToleratedA2uiOpenLine` → `if (true) return false;`

```
Com falha! – Com falha: 11, Aprovado: 2987, Total: 2998

BrokenFence_RealTurn1025_IsRecognizedAndExtractsTheCard      <- O CASO REAL
RealTurn1025_ProducesAValidatableCard                        <- O CASO REAL
AcceptedVariations_AllOpenTheBlock("=\"a2ui\"") | ("a2ui") | ("=a2ui") | ("``a2ui") | ("`a2ui") | ("=\"A2UI\"")
AcceptedVariation_WithSurroundingSpaces_OpensTheBlock
KnownLimit_AnIsolatedA2uiLineIsConsumedEvenInLegitimateProse
IsToleratedA2uiOpenLine_RequiresAValidA2uiLineRightAfter
```

**Tem dentes.** As **duas** provas do caso real reprovam, e as **7 formas autorizadas** idem.

**Restauro:** byte-idêntico OK · `MUTACAO2` ausente OK · SHA256 idêntico OK.

### Confirmação final pós-restauro

```
Aprovado!  – Com falha: 0, Aprovado: 2998, Total: 2998
git diff: VAZIO (nenhum marcador de mutação)
```

---

## 4. O CONTRATO DE TOLERÂNCIA (verificado linha a linha)

| Regra | Implementação | Verificação |
|---|---|---|
| **Oficial decidido ANTES, SEM a trava** | `A2uiOfficialOpenRegex` (linhas 36-37); abre direto (117-126) | 3 testes verdes, inclusive **abertura oficial + linha em branco**, que seria a regressão do req 3 |
| **Tolerada exige a linha INTEIRAMENTE cerca** | `A2uiBrokenOpenRegex` (54-56) | Prosa com menção, 'a2ui' no meio de frase, 4 crases, '= a2ui =' → não abrem |
| **Condição 2 obrigatória** | linhas 191-192 | M1 prova (5 reprovações) |
| **Fail-safe na última linha** | `nextLine = null` → 191 false | teste verde |
| **Não pula linhas vazias** | idem 191 | PlausibleFence_FollowedByBlankLine verde |

**A7 (o teste mais valioso, pedido do Lead):** tolerada + JSON válido com **prosa antes e depois** → a prosa das **duas pontas** sobrevive e só o bloco sai. Simulei fielmente: prosa ANTES preservada OK, prosa DEPOIS preservada OK, JSON removido OK.

---

## 5. LIMITE CONHECIDO (documentado, não é defeito)

Um `a2ui` **isolado** numa linha é **indistinguível** de prosa — a forma da linha não separa os dois significados; **só a condição 2 decide**. Consequência: prosa legítima que consista exatamente na palavra `a2ui` seguida de um JSON A2UI válido abre bloco.

Isso é **inerente ao escopo autorizado** (a forma "a2ui isolado" foi aprovada) e está **documentado pelo próprio testador** em `KnownLimit_AnIsolatedA2uiLineIsConsumedEvenInLegitimateProse`. Com a condição 2 presente, o risco é residual e aceito. **Não reporto como defeito.**

---

## 6. DEFEITOS E NITs

| ID | Severidade | Descrição | Estado |
|---|---|---|---|
| — | — | Nenhum defeito **bloqueante** | — |
| DEF-1 | **Bloqueante** | **Defeito ampliado pela própria correção**: raiz JSON não-objeto em `IsValidA2uiMessage` lançava `InvalidOperationException` que escapava de `Extract()` (a tolerância virou caminho de exceção) | **CORRIGIDO** na mesma onda (guardas 528 e 349); provado por mim |
| NIT-1 | Baixa | `a2ui` isolado + JSON válido abre bloco em prosa legítima (§5) | Limite aceito, documentado |
| NIT-2 | Baixa | `catch (JsonException)` continua sendo o único catch nas funções de parse — hoje seguro **por causa** das guardas de `ValueKind`; depende da invariante | Registrado |

---

## 7. ARMADILHAS METODOLÓGICAS

1. **Pipe do PowerShell dá exit=1 espúrio** → todas as suítes capturadas em **ARQUIVO**.
2. **`git -C <subprojeto>`** — a raiz do workspace **não** é repo git (exit=128); `DiscoveryRMM_API` é. Um comando na raiz devolveria vazio, indistinguível de árvore limpa.
3. **`M` no `git status` != resquício de mutação** — é o trabalho não commitado das tasks. A prova de restauro é **SHA256 idêntico + ausência dos marcadores** no diff.
4. **O arquivo mudou de pasta** durante a entrega (`Services/` → `Services/Ai/`); meu primeiro grep falhou por isso — não interpretei como arquivo ausente.
5. **Nenhum dos 2 flakes conhecidos apareceu** nesta rodada; não reportei nenhum deles.

---

## 8. Reprodutibilidade

```powershell
cd C:\Projetos\RMM_Discovery\DiscoveryRMM_API
dotnet build Discovery.slnx 2>&1 | Out-File -Encoding utf8 build.txt
dotnet test Discovery.slnx --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E" 2>&1 | Out-File -Encoding utf8 test.txt

# identidade do arquivo (prova de restauro)
(Get-FileHash src\Discovery.Infrastructure\Services\Ai\AiChatA2uiExtractor.cs -Algorithm SHA256).Hash
# esperado: 949B9FDF528847D5E0F577F633523328B6843668106D2589D9886AE5B9828ADF
```

**Evidência do caso real preservada:** `C:\Windows\Temp\turn1025_assistant.txt` (conteúdo integral do turno 1025, extraído do `chat.db`) e `turn1025_first4.txt` (cerca + 2 JSON + fechamento).

**Integridade:** nenhum arquivo de produção alterado por mim. As 2 mutações foram revertidas **byte a byte** (comparação direta + SHA256) e o `git diff` do subprojeto **não contém nenhum marcador de mutação**. Escrevi apenas este relatório.
