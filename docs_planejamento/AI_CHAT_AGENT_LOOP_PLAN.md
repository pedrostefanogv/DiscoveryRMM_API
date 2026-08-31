# PLANO — Agent Loop Resiliente do Chat IA

> Status: FASES 1, 2, 3, 5 e 6 EXECUTADAS (2026-08-31). Fase 4 (agent Go) pendente.
> Objetivo: eliminar os casos em que o chat IA "para/trava" sem responder e o
> usuário precisa mandar "continue" — garantindo que cada solicitação seja
> processada por completo até uma resposta final.

## 1. Diagnóstico (causas do travamento)

| #   | Causa                                                                                                                                                           | Onde                                                                                                  |
| --- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| 1   | Orçamento de iterações baixo e clampado em 1–10 (`MaxToolCallIterations`, default 3 na config / 5 no constant). Loop esgota e quebra em silêncio.               | `AiChatStreamingOrchestrator.StreamAsync` / `StreamMultiRoundAsync`, `AiChatService.ProcessSyncAsync` |
| 2   | Break silencioso por KB vazia (`consecutiveEmptyKbSearches >= 2`) sem avisar LLM/usuário.                                                                       | idem                                                                                                  |
| 3   | Round delegado ao agent pode morrer no caminho: servidor faz `yield break` após `round_end` e depende do agent reenviar `ToolResults`. Sem timeout/recuperação. | fim do bloco `tool_calls`                                                                             |
| 4   | Retry de conteúdo vazio fraco: só roda com `toolIterations > 0`, 1 tentativa, e não existe no fluxo sync.                                                       | `streamDone`                                                                                          |
| 5   | Sem sinal de progresso entre rounds (sem heartbeat) — frontend não sabe se está processando ou morto.                                                           | SSE                                                                                                   |

## 2. Decisões de design (revisão 2026-08-31)

1. **Um único setting**: reaproveitar `MaxToolCallIterations` (não criar setting
   novo). Default passa a **10**, clamp ampliado para **1–20**.
2. **Síntese forçada**: ao esgotar o orçamento com o LLM ainda querendo tools,
   injeta nota de sistema e faz chamada final **sem tools** (até 2 tentativas).
   Aplica também ao fluxo sync e ao caso `toolIterations == 0` com conteúdo vazio.
3. **Chunk de progresso retrocompatível**: campos opcionais `LoopRound` /
   `LoopMaxRounds` em `AiChatStreamChunk` — agent antigo ignora (JSON), sem
   deploy sincronizado.
4. **Watchdog leve (sem hosted service)**: `IMemoryCache` registra
   `pending_round:{sessionId}` (TTL 120s) ao emitir `round_end` com tool call
   pendente. No próximo `StreamMultiRoundAsync` da sessão, entrada expirada →
   nota de sistema "execução no agent expirou" e o stream conclui com resposta.
5. **KB vazia**: em vez de break seco, nota de sistema + **remoção da tool
   `knowledge_search`** do `availableTools` do round seguinte (mais
   determinístico que só instruir via texto).
6. **Bug latente corrigido**: `contentBuilder` com resíduo de tokens no retry —
   limpar builder antes do retry / builder dedicado.
7. **Defesa principal no agent Go** (repo `C:\Projetos\Discovery`, fase separada):
   timer de 60s pós-`round_end`; se não conseguir enviar tool results, envia
   `{"error":"agent tool execution timeout"}` como resultado.

## 3. Fases

| Fase | Escopo                                                                                                                         | Status |
| ---- | ------------------------------------------------------------------------------------------------------------------------------ | ------ |
| 1    | Config: default 10, clamp 1–20, helper `ResolveMaxToolIterations()`                                                            | ✅     |
| 2    | Loop resiliente C#: síntese forçada (2 tentativas, também no sync), KB vazia → nota + remoção da tool, correção contentBuilder | ✅     |
| 3    | Chunk `loop_progress` (`LoopRound`/`LoopMaxRounds`) emitido por iteração                                                       | ✅     |
| 4    | Agent Go + Frontend (repo Discovery): parsing `loop_progress`, UI "round X/Y", timer 60s pós-round_end                         | ✅     |
| 5    | Watchdog leve: `IMemoryCache` round pendente + nota de expiração                                                               | ✅     |
| 6    | Testes C#                                                                                                                      | ✅     |

## 4. Arquivos alterados (servidor C#)

- `src/Discovery.Core/ValueObjects/AIIntegrationSettings.cs` — doc do setting.
- `src/Discovery.Infrastructure/Services/Ai/AiChatConstants.cs` — default 10, limites.
- `src/Discovery.Infrastructure/Services/Ai/AiChatHelpers.cs` — `ResolveMaxToolIterations`, `BuildSynthesisNote`, constantes de notas de sistema.
- `src/Discovery.Core/DTOs/AiChatDtos.cs` — `LoopRound`/`LoopMaxRounds` no chunk.
- `src/Discovery.Infrastructure/Services/Ai/AiChatStreamingOrchestrator.cs` — loop resiliente nos 2 fluxos + watchdog.
- `src/Discovery.Infrastructure/Services/AiChatService.cs` — sync com síntese forçada.
- `src/Discovery.Tests/...` — testes novos.

## 4a. Arquivos alterados (agent Go — C:\Projetos\Discovery\src)

- `app/core/ai/chat_stream.go` — campos `LoopRound`/`LoopMaxRounds` no struct do evento SSE.
- `app/core/ai/chat_multi_round.go` — `SendStreamMultiRoundWithProgress` (wrapper com callback de progresso), case `loop_progress` no parser SSE, timer de 60s por execução de tool (`context.WithTimeout` sobre streamCtx).
- `app/services/chat/service.go` — emite evento Wails `chat:loop_progress` {round, maxRounds}.
- `frontend/js/app-chat.js` — handler `onChatLoopProgress`: indicador "Pensando... (round X/Y)" no bubble de streaming.
- `app/core/ai/chat_loop_progress_test.go` — 2 testes novos (parsing com/sem callback).

## 4b. Validação agent Go

- `go build ./app/core/... ./app/services/...` OK; `go vet` OK.
- `go test ./app/core/ai/... ./app/core/mcp/... -count=1` OK (inclui 2 testes novos de loop_progress).

## 4c. Pendências futuras

- Gerência da config `MaxToolCallIterations` na UI admin do servidor (setting já persiste — só expor).
- Bindings Wails regenerados não são necessários (evento via emitEvent, não binding).

## 5. Validação

- `dotnet build Discovery.slnx` — 0 erros.
- Testes novos: `AiChatAgentLoopTests` — 16/16 aprovados.
- Compatibilidade: fases 1–3 e 5 retrocompatíveis com agent antigo.

## 6. Notas de implementação

- `InternalsVisibleTo("Discovery.Tests")` adicionado ao csproj da Infrastructure
  (AiChatHelpers/AiChatConstants são internal).
- `IMemoryCache` injetado no `AiChatStreamingOrchestrator` (DI resolve automaticamente).
- Síntese forçada dispara apenas quando o conteúdo final está vazio (não
  sobrescreve resposta válida já produzida).
- Watchdog: chave `pending_round:{sessionId}` no IMemoryCache, TTL 120s;
  removida e convertida em nota de sistema no próximo multi-round da sessão.

## 7. Revisão final (2026-08-31) — bugs corrigidos e melhorias aplicadas

Bugs corrigidos na revisão anterior:

1. (Go) `onLoopProgress` nunca chegava ao parser (`executeRound` passava nil).
2. (C#) Watchdog invertido: nota "expirou" injetada no multi-round normal.
3. (C#) `StreamAsync` não registrava `pending_round` no watchdog.
4. (C#) Branch de streaming usava `availableTools` em vez de `roundTools` com KB esgotada.
5. (Go) Loop multi-round hardcoded em 5 rounds vs servidor até 20 → `maxMultiRounds = 20`.

Bugs/melhorias desta revisão final: 6. (C#) **Ordem pós-loop unificada** no multi-round: sanitização → A2UI → síntese
forçada (antes a síntese rodava antes da sanitização, desperdiçando tentativas
quando o conteúdo era só vazamento que a sanitização esvaziaria). 7. (Go) **Deadline total de 10min** no loop multi-round (`maxMultiRoundTotal`):
com 20 rounds × timeout progressivo (até 130s) + 60s por tool, o pior caso
sem deadline passaria de 40min. Encerra com resposta parcial + log. 8. (C#) Heartbeat `loop_progress` só emitido quando há tools (single-pass sem
tools não tem loop — heartbeat seria enganoso).

Validação final: `dotnet build` 0 erros, `AiChatAgentLoopTests` 16/16;
`go build`/`go vet`/`go test ./app/core/ai/...` OK.
