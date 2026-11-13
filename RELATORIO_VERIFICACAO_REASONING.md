# RELATÓRIO DE VERIFICAÇÃO INDEPENDENTE — task-25 (vazamento de raciocínio)

**Verificador:** teammate `verifier` (task-25, rev 2) | **Workspace:** C:\Projetos\RMM_Discovery
**Escopo:** DiscoveryRMM_API (.NET) | **Arquivo alvo:** `src/Discovery.Infrastructure/Services/OpenAiProvider.cs`

## Veredito

**APROVADO — nenhum defeito bloqueante.** As duas camadas estão implementadas e verificadas, o **caso real** (turno 1046) foi reproduzido a partir do banco, e o ataque principal (4c: raciocínio no texto **persistido**) foi **refutado por estrutura e por teste**.

| Verificação | Resultado |
|---|---|
| `dotnet build Discovery.slnx` | **0 Erros**, 6 Avisos |
| `dotnet test` (suite completa) | **0 falhas, 3015 aprovados** |
| Mutação M1 (raciocínio volta a virar token) | **7 testes REPROVARAM** |
| Restauro | **SHA256 idêntico** (`C8158607…66F9C`) + nenhum marcador no diff |
| Falhas conhecidas | **nenhuma** (nem os 2 flakes apareceram) |

### Contagem 3015 conferida de forma independente
Rodei as três fatias separadamente: `AiReasoningLeakTests` = **15**, `AiChatA2uiFenceToleranceTests` = **28**, e o **resto** (filtro excluindo as duas) = **2972**. **2972 + 28 + 15 = 3015.** Fecha exatamente com a aritmética corrigida pelo Lead.

> O valor `2998` que circulou antes era o **total da onda anterior**, que **já incluía** os 26 casos da classe de cerca — por isso gerava um "+2 órfão" inexistente. O test-author fez certo ao reportar como **não atribuído** em vez de inventar causa.

---

## 1. CASO REAL REPRODUZIDO (chat.db, turno 1046)

Fonte: `C:\ProgramData\Discovery\logs\chat.db` (1046 linhas). `id=1046`, coluna `assistant`, `ts=2026-11-13T00:01:41.716Z`, `type=multi_round_done`.

| Métrica | Medido | Brief |
|---|---|---|
| Total | **3890 chars** | 3890 |
| CoT (antes de 'Fiz uma verifica') | **2167 chars (55.7%)** | 2167 (56%) |
| Resposta real | **1723 chars (44.3%)** | — |

Início do CoT literal: `"memory.search" is not in my tool list. Actually I don't have memory.search tool...' ` — **bate** com o brief. Conteúdo preservado em `C:\Windows\Temp\turn1046_assistant.txt`.

### 1.1 CONFIRMO a análise do Lead: o CoT estava no campo `content`
Marcadores de separação no texto: `<think`=**0** | `</think`=**0** | `<thinking`=**0** | `reasoning`=**0** | `---`=**0**.

A transição CoT→resposta é **apenas uma quebra de parágrafo**, sem nenhum separador estrutural: `'...less risk.\n\nLet me write it'` seguido de `'Fiz uma verificação...'`.

O CoT tem **forma de raciocínio de ferramenta** ("memory.search is not in my tool list", "Let me check memory_list maybe?", 14 linhas de deliberação) **inteiramente dentro do texto que virou resposta**.

> **Conclusão:** a **camada 2** (ignorar `delta.reasoning`) é **defesa em profundidade** — ela protege a classe "provedor manda reasoning em campo separado", que **não é este caso**. A **camada 1** (controle explícito do payload) é quem ataca a **causa** do turno 1046.

---

## 2. CAMADA 1 — controle explícito do payload

`AddReasoningToPayload` (linhas 311-331) envia **SEMPRE**, e **só para OpenRouter** (guarda na :317):

```csharp
payloadDict["reasoning"] = new Dictionary<string, object?>
{
    ["effort"]  = effort,   // default "high"
    ["exclude"] = true,     // impede o CoT de voltar na resposta
};
```

### 2.1 SHAPE VERIFICADO CONTRA A DOCUMENTAÇÃO OFICIAL
Fonte: [openrouter.ai/docs/use-cases/reasoning-tokens](https://openrouter.ai/docs/use-cases/reasoning-tokens) (fetch direto, HTTP 200).

**Citação 1 — `exclude` é o campo de Chat Completions (o nosso caso):**
> "On Chat Completions they appear in the `reasoning` field of each message, and `reasoning.exclude` can omit them."
> "…`exclude` is a Chat Completions-only option…"

**Citação 2 — `include_reasoning` é LEGADO, e a doc recomenda o parâmetro unificado:**
> "For backward compatibility, Chat Completions still accepts the following legacy parameters…"
> `include_reasoning: false` — Equivalent to `reasoning: { exclude: true }`
> "However, we recommend using the new unified `reasoning` parameter for better control and future compatibility."

**Veredito: OK — sem defeito de shape.** O código usa o parâmetro **unificado recomendado**, exatamente equivalente ao que a doc descreve. O brief citava `include_reasoning` (legado, ainda aceito mas **não recomendado**); o dono escolheu o recomendado.

**Citação 3 — confirma por que a camada 1 precisa ser explícita:**
> "Reasoning tokens are included in the response by default if the model decides to output them."

### 2.2 Demais pontos da camada 1 (todos OK)
| Ponto | Verificação |
|---|---|
| `effort` default | `"high"` (constante `DefaultReasoningEffort`), que é o default do próprio modelo no catálogo medido |
| `"none"` | **explicitamente barrado** por comentário (:342-343): desligaria o raciocínio — alinhado com a decisão do usuário de **não desligar** |
| Valor inválido na config | cai no default em vez de arriscar 400 — a config é texto livre da UI |
| Provedores não-OpenRouter | guarda na :317 antes de tocar no payload; os **3 call sites** usam o mesmo helper |

---

## 3. CAMADA 2 — descarte do raciocínio no parser

`TryGetReasoningDelta` (:790-803) reconhece **as duas chaves**: `"reasoning"` (documentada) e `"reasoning_content"` (variante de gateways compatíveis). O chamador (:892-898) **lê apenas para log** e **não retorna evento** — o comentário declara que o valor é reconhecido de propósito e **nunca somado ao content**.

O `content` mantém **prioridade sobre `finish_reason`** (:903-908), preservando o caso `content + finish` e a ordem sensível das tool calls (B16-r2).

---

## 4. O ATAQUE PRINCIPAL: 4c — RACIOCÍNIO NO TEXTO PERSISTIDO

Pergunta: o raciocínio pode sumir da **tela** e ainda assim entrar no texto **salvo**?

### 4.1 Prova estrutural (leitura)
Varri todos os usos de `reasoning` no `Discovery.Infrastructure`: o valor extraído (`reasoningText`) aparece em **exatamente 2 lugares** — a chamada na :892 e a **interpolação do log** na :897. **Não há atribuição a nenhum acumulador.**

Os pontos de acumulação de conteúdo (`contentBuilder.Append`, `fullContent =`, `streamedChars +=`) consomem **apenas** `evt.Content`/`token` vindos de eventos do tipo `"token"`. Como o raciocínio **nunca retorna** um evento `"token"`, **não existe caminho** para ele chegar ao persistido.

### 4.2 Prova por teste
- `ReasoningOnlyChunk_NeverProducesAVisibleToken` — chunk só com reasoning não gera token visível;
- `ChunkWithContentAndReasoning_EmitsOnlyTheContentToken` — só o content vira token;
- `ChunkWithReasoningBeforeContentInJson_StillEmitsOnlyContent` — **ordem no JSON não engana**;
- `ReasoningContentChunk_VariantKeyAlsoNeverProducesAToken` — a variante `reasoning_content` também é descartada;
- `AllBudgetSpentOnReasoning_ReturnsDoneWithLength_AndNoToken` — orçamento todo gasto em raciocínio devolve `done` com `length` e **nenhum token**.

**Todos reprovam na M1** (§5), o que prova que o comportamento é **guardado por teste**, não apenas por leitura.

> **Veredito 4c: REFUTADO.** O raciocínio não entra no texto persistido — não por sorte, mas porque **não há caminho de código** para isso, e os testes travam o comportamento.

---

## 5. MUTAÇÃO M1

Reintroduzi a emissão do raciocínio como token (substituí o `Debug.WriteLine` por `return new LlmStreamEvent(Type: "token", Content: reasoningText)`).

```
Com falha! – Com falha: 7, Aprovado: 3008, Total: 3015

ReasoningOnlyChunk_NeverProducesAVisibleToken
ReasoningContentChunk_VariantKeyAlsoNeverProducesAToken
ChunkWithContentAndReasoning_EmitsOnlyTheContentToken
ChunkWithReasoningBeforeContentInJson_StillEmitsOnlyContent
ReasoningWithFinishReason_DoesNotLoseTheFinish
ReasoningWithToolCalls_KeepsTheToolCallDeltasIntact        <- ordem sensível B16-r2
AllBudgetSpentOnReasoning_ReturnsDoneWithLength_AndNoToken
```

**Tem dentes: 7 reprovações — exatamente a contagem independente do test-author (7).** Sem fragilidade. Cobrem os itens (a), (b) e (d) do meu ataque original.

**Restauro:** byte-idêntico OK · `MUTACAO1` ausente OK · **SHA256 = `C815860792A2B00B6522D1DC9682C8C4BE9C6F38E2B470C3F064AA9609366F9C`** (idêntico ao freeze) · `git -C DiscoveryRMM_API diff` sem marcadores (**VAZIO**).

**Suíte final pós-restauro:** `Com falha: 0, Aprovado: 3015, Total: 3015`.

---

## 6. ITEM NOVO (pedido do Lead): content vazio + `finish_reason=length`

**VERIFICADO-OK.** O usuário **não** fica com resposta em branco silenciosa. Mecanismo em `AiChatStreamingOrchestrator.cs:896-945`, **três camadas**:
1. **:901-908** — se `fullContent` está vazio, injeta nota de sistema (`SynthesisBudgetNote` ou `EmptyContentNote`);
2. **:910-937** — loop de **síntese forçada**: até `MaxSynthesisRetries` chamadas **sem tools**, emitindo tokens; sai assim que houver conteúdo;
3. **:939-944** — se ainda vazio, **fallback visível**: *"Não foi possível gerar uma resposta. Tente reformular sua pergunta ou entre em contato com o suporte."*

O provider também **loga warning** em `finish_reason=length` (:765-770) e quando o stream termina **sem** `[DONE]`/`finish_reason` (:778-786) — os dois casos ficam visíveis no log.

**Registro como CUSTO, não defeito:** a síntese forçada consome até 2 chamadas extras. Com `exclude=true` e um modelo que gasta todo o `max_tokens` pensando, a resposta pode demorar e custar mais — mas o usuário **recebe algo**.

---

## 7. RISCO RESIDUAL CONHECIDO E ACEITO (decisão do usuário)

O caso do turno 1046 é CoT **dentro do `content`**. A camada 1 ataca a causa pedindo `exclude` ao provedor; **se o provedor ignorar o pedido e continuar misturando**, o CoT voltaria a aparecer — e não há camada 3 (heurística) para limpar, por **decisão explícita do usuário**.

Registro como **RISCO RESIDUAL CONHECIDO E ACEITO**, com o cenário concreto (turno 1046, 2167 de 3890 chars). **Não é defeito a consertar** — a camada 3 foi recusada por gerar falso positivo, o que é uma decisão consciente e defensável.

Mitigação já existente: a camada 2 cobre a classe "reasoning em campo separado", e o parser reconhece **as duas** chaves conhecidas.

---

## 8. DEFEITOS E NITs

| ID | Severidade | Descrição | Estado |
|---|---|---|---|
| — | — | Nenhum defeito **bloqueante** | — |
| RES-1 | Média | Risco residual aceito: CoT dentro do `content` se o provedor ignorar `exclude` (§7) | Decisão do usuário, documentado |
| CUSTO-1 | Baixa | Síntese forçada custa até 2 chamadas extras quando o conteúdo vem vazio (§6) | Comportamento intencional |
| NIT-1 | Baixa | O provider loga o descarte do raciocínio em `Debug.WriteLine` (:896) — nível Debug, não aparece no log de produção. Um `LogDebug` estruturado daria rastreabilidade | Recomendação |

---

## 9. ARMADILHAS METODOLÓGICAS

1. **Pipe do PowerShell dá exit=1 espúrio** → todas as suítes capturadas em **ARQUIVO**.
2. **`git -C <subprojeto>`** — a raiz do workspace **não** é repo git; `DiscoveryRMM_API` é.
3. **`M` no `git status` != resquício de mutação** — é o trabalho não commitado das tasks. Prova de restauro = **SHA256 idêntico + ausência dos marcadores**.
4. **Primeira tentativa de M1 não aplicou** (a âncora de texto não casou por diferença de espaços/CRLF). Eu **matei o job antes de rodar**, confirmei que o arquivo estava intacto e refiz a mutação **por índice de linha**, conferindo a aplicação antes de executar. Reportar o resultado daquela rodada teria sido um falso "0 reprovações".
5. **Nenhum dos 2 flakes conhecidos apareceu**; não reportei nenhum.
6. **Não criei teste heurístico de detecção de CoT** — decisão explícita do Lead, respeitada.

---

## 10. Reprodutibilidade

```powershell
cd C:\Projetos\RMM_Discovery\DiscoveryRMM_API
dotnet build Discovery.slnx 2>&1 | Out-File -Encoding utf8 build.txt
dotnet test Discovery.slnx --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E" 2>&1 | Out-File -Encoding utf8 test.txt

# identidade do arquivo (prova de restauro)
(Get-FileHash src\Discovery.Infrastructure\Services\OpenAiProvider.cs -Algorithm SHA256).Hash
# esperado: C815860792A2B00B6522D1DC9682C8C4BE9C6F38E2B470C3F064AA9609366F9C
```

**Evidência do caso real preservada:** `C:\Windows\Temp\turn1046_assistant.txt` (conteúdo integral do turno 1046, extraído do `chat.db`).

**Integridade:** nenhum arquivo de produção alterado por mim. A mutação foi revertida **byte a byte** (comparação direta + SHA256) e o `git diff` do subprojeto **não contém marcador de mutação**. Escrevi apenas este relatório.
