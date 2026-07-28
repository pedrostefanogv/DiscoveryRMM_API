# Plano de Melhoria — AI Chat (Agent ↔ API)

> **Data:** 2026-07-18
> **Status:** Fase 1 ✅ | Fase 2 ✅ | Fase 3 ✅ (API + Agent código)
> **Commits:** ~13 commits no branch `dev` | **Servidor:** 192.168.1.120 deployed

### ✅ Concluído & Deployed (API)

| # | Item | Status |
|---|------|--------|
| 2.1 | Encoding UTF-8 | ✅ |
| 2.2 | Retry respostas vazias + fallback | ✅ |
| 2.3 | SessionId no stream | ✅ |
| 2.4 | System prompt melhorado | ✅ |
| 2.5 | Loop tools: dedup KB, maxIter=2, break-early | ✅ |
| 2.6 | XML fallback condicional | ✅ |
| 2.7 | Campos feedback (M060) | ✅ |
| 2.8 | KB embedding: 401 corrigido, auto-sync dimensão | ✅ |
| 2.9 | Quick-reply cache | ✅ |
| 2.16 | time.current + sequential_thinking | ✅ |
| 2.13 | API: ChatStreamCommand, StreamMultiRoundAsync, /me/agent-tools/registry | ✅ |
| — | SSE: tool_call, round_end | ✅ |
| 2.1 | Encoding UTF-8 no StreamAsync (sem tools) | ✅ |
| 2.8 | MinSimilarityScore 0.65→0.55 | ✅ |
| 2.16 | memory.search handler funcional (busca em ai_chat_messages) | ✅ |

### ✅ Código pronto (Agent — C:\Projetos\Discovery, commit `32ca534`)

| # | Item | Status |
|---|------|--------|
| 2.14 | `chat_multi_round.go`: SendStreamMultiRound, executeRound, parseMultiRoundSSE | ✅ |
| 2.14 | `mcpExecuteForChat` usa MCP registry local (~30 tools) | ✅ |
| 2.14 | `RegisterAgentToolsOnServer` envia tools para API | ✅ |
| 2.14 | `StartChatStream` usa `SendStreamMultiRound` | ✅ |
| 2.14 | `chat_stream.go` atualizado com fields: ToolCallID, ToolName, ToolArguments | ✅ |

### ❌ Pendente

| # | Item |
|---|------|
| 2.8 | Popular KB com artigos (3 artigos de teste existentes no site) |
| — | Deploy do agent compilado nos endpoints |

> **Escopo:** `DiscoveryRMM_API` (servidor) + `Discovery` (agent)

---

## 1. Diagnóstico — Problemas Identificados

### 1.1 Renderização de Respostas (Encoding / Mojibake)

**Sintoma nos logs:** As respostas persistidas no banco exibem caracteres corrompidos:
- `├ôtimo!` (Ótimo!)
- `Vou gui├íÔÇælo` (Vou guiá-lo)
- `1´©ÅÔâú` (emoji 1️⃣)
- `ÔåÆ` (→)
- `ÔÇ£` / `ÔÇØ` (aspas " ")

**Causa raiz:** O modelo `openai/gpt-oss-20b` (via OpenRouter) retorna tokens em UTF-8, mas há **duplo encoding** em algum ponto do pipeline:
1. O `OpenAiProvider.StreamWithToolsAsync` usa `StreamReader` sem explicitar `Encoding.UTF8` no construtor (usa default do framework que pode variar).
2. O `JsonSerializer.Serialize(chunk)` no controller pode não preservar UTF-8 corretamente em alguns cenários de BOM.
3. O `EscapeSse()` no controller faz escape manual de `\n` e `\r` mas não trata caracteres multi-byte.

**Severidade:** Alta — afeta diretamente a experiência do usuário.

### 1.2 Respostas Vazias

**Sintoma:** Sessão `019f70ef-afef` — usuário perguntou "Como instalar o foxit reader", a IA fez **3 chamadas** a `knowledge_search` (todas retornaram `found:false`), e a resposta final do assistant foi **string vazia** (`content=''`, `tokens_used=12040`).

**Causa raiz:**
1. O modelo consumiu todos os tokens em raciocínio interno mas não produziu texto visível.
2. O `AiChatService.StreamAsync` **não detecta nem trata respostas vazias** — persiste `content=''` como mensagem do assistant e emite `done`.
3. Não há retry nem fallback quando o conteúdo final é vazio após o loop de tool calls.

**Severidade:** Alta — o usuário fica sem resposta.

### 1.3 Tool `knowledge_search` Sempre Vazia

**Sintoma:** Em **todos** os 20+ logs analisados, `knowledge_search` retornou `{"found":false,"message":"Nenhum artigo encontrado na base de conhecimento."}`.

**Causa raiz:**
1. A base de conhecimento está vazia ou sem artigos indexados com embeddings.
2. O `MinSimilarityScore` padrão (0.65) pode estar alto demais para o modelo de embedding usado.
3. A IA chama `knowledge_search` repetidamente (até 3x na mesma sessão) mesmo recebendo `found:false`, desperdiçando iterações.

**Severidade:** Média — não bloqueia, mas gera latência desnecessária (12-33s por chamada) e consome tokens.

### 1.4 Tool `shell` — IA Alucina Ferramenta Inexistente

**Sintoma:** A IA tentou chamar `shell` em 2 sessões (`019f70d4`, `019f6f74`), recebendo `{"error": "Tool 'shell' não autorizada para este escopo."}`.

**Causa raiz:**
1. A tool `shell` **não existe** no `McpToolExecutor` — não tem handler, não tem descrição, não está na lista de tools enviadas ao LLM.
2. O modelo `gpt-oss-20b` alucina a existência de uma tool `shell` porque o system prompt diz "Use comandos específicos quando aplicável" e "Se precisar executar ferramentas (comandos, scripts), informe claramente antes".
3. O system prompt **não lista as ferramentas disponíveis** nem suas capacidades — a IA não sabe o que pode ou não fazer.

**Severidade:** Média — a IA tenta executar comandos que não pode, gerando respostas confusas.

### 1.5 Ferramentas MCP com Descrição mas Sem Implementação

**Sintoma:** O `McpToolExecutor.GetToolDescription` lista 6 tools, mas **apenas `knowledge_search` tem handler**. As outras 5 (`filesystem.read_file`, `postgres.query`, `time.current`, `memory.search`, `sequential_thinking`) retornam `"não tem implementação registrada"` se chamadas.

**Atenuação:** `GetAvailableToolsAsync` filtra apenas tools com handler, então o LLM não as vê. Mas as policies existem no banco habilitadas (`is_enabled=t`), criando confusão administrativa.

**Severidade:** Baixa — não afeta o usuário, mas é débito técnico.

### 1.6 XML Fallback Sem Follow-up LLM

**Sintoma:** O `ParseAndExecuteXmlToolCallsAsync` executa tools XML encontradas no texto, mas **não faz nova chamada LLM** com os resultados — o texto limpo (sem os blocos XML) é a resposta final.

**Causa raiz:** O XML fallback roda **após** o loop de streaming, fora do ciclo de iteração. Os resultados das tools são adicionados a `llmMessages` mas nunca enviados de volta ao LLM.

**Severidade:** Média — se o modelo emitir tool calls via XML (comum em modelos menores), os resultados são ignorados.

### 1.7 Sessão Sempre Nova no Stream

**Sintoma:** O `AgentAuthController.ChatStream` sempre passa `sessionId: null` para `StreamAsync`, criando uma nova sessão a cada mensagem.

**Causa raiz:** O controller ignora o `SessionId` do `ChatAsyncCommand` no endpoint de stream.

**Impacto:** O histórico é carregado por sessão, mas como cada stream cria sessão nova, **o histórico nunca é recuperado** — a IA não tem contexto de mensagens anteriores na mesma conversa.

**Severidade:** Alta — a IA "esquece" tudo entre mensagens.

### 1.8 Latência Alta e Variável

**Sintoma nos logs:**
- "oi" → 1.4s a 18.9s (variância enorme)
- "Quero instalar o foxit reader" → 21s
- "Quero instalar o foxit na minha maquina" → 33s (com 2x knowledge_search vazias)

**Causas:**
1. Loop de tool calls com `knowledge_search` sempre falhando adiciona 5-10s por iteração.
2. Modelo `gpt-oss-20b` via OpenRouter tem latência variável.
3. Não há cache de respostas para mensagens idênticas ("oi" repetido).

### 1.9 System Prompt Não Lista Capacidades Reais

**Sintoma:** O system prompt diz "Se precisar executar ferramentas (comandos, scripts), informe claramente antes" mas **não há tool de execução de comandos**. A IA acredita que pode executar comandos e tenta (tool `shell` alucinada) ou dá instruções de PowerShell que o usuário teria que executar manualmente.

**Impacto:** A IA promete ações que não pode cumprir ("Vou verificar se o Firefox está instalado" → falha → "Infelizmente não consigo executar comandos").

### 1.10 Bridge de Chat com Parser SSE Divergente

**Sintoma:** O `chat_bridge.go` tem um parser SSE **diferente** do `ai/chat_stream.go` — trata cada `data:` line como string crua em vez de JSON. Se o bridge for usado, tool_call events e metadados são perdidos.

**Severidade:** Baixa (bridge é fallback), mas é inconsistência técnica.

### 1.11 Modelo Não Registrado na Persistência

**Sintoma:** O usuário mencionou ter testado "modelos diversos", mas o campo `model_version` no banco sempre mostra `openai/gpt-oss-20b`. Não há tracking de quais modelos foram testados nem suas métricas de qualidade.

---

## 2. Plano de Melhoria

### Fase 1 — Correções Críticas (Alta Prioridade)

#### 2.1 Corrigir Encoding UTF-8 no Pipeline de Streaming

**Arquivos:**
- `src/Discovery.Infrastructure/Services/OpenAiProvider.cs` — `StreamWithToolsAsync`
- `src/Discovery.Api/Controllers/AgentAuthController.cs` — `ChatStream`

**Ações:**
1. No `OpenAiProvider.StreamWithToolsAsync`, instanciar `StreamReader` com `Encoding.UTF8` explícito:
   ```csharp
   using var reader = new StreamReader(stream, Encoding.UTF8);
   ```
2. No `AgentAuthController.ChatStream`, usar `JsonSerializerOptions` com `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` para preservar caracteres UTF-8:
   ```csharp
   private static readonly JsonSerializerOptions SseJsonOptions = new()
   {
       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
   };
   ```
3. Remover o `EscapeSse()` manual e usar `JsonSerializer.Serialize(chunk, SseJsonOptions)` para todos os chunks, incluindo erros.
4. Garantir `Response.Headers.ContentEncoding = "utf-8"` no SSE.

#### 2.2 Tratar Respostas Vazias com Retry

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs` — `StreamAsync`

**Ações:**
1. Após o loop de tool calls, verificar se `fullContent` é vazio ou whitespace:
   ```csharp
   if (string.IsNullOrWhiteSpace(fullContent) && toolIterations > 0)
   {
       // Retry sem tools, com mensagem explícita
       llmMessages.Add(new LlmMessage("user",
           "[SISTEMA] A resposta anterior ficou vazia. Forneça uma resposta direta e útil ao usuário."));
       // Nova chamada LLM sem tools
       await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, retryOptions, ct))
       {
           contentBuilder.Append(token);
           yield return new AiChatStreamChunk(Type: "token", Content: token);
       }
       fullContent = contentBuilder.ToString();
   }
   ```
2. Se ainda vazio após retry, emitir mensagem padrão:
   ```csharp
   if (string.IsNullOrWhiteSpace(fullContent))
   {
       fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta.";
       yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
   }
   ```
3. Limitar a 1 retry para evitar loops.

#### 2.3 Corrigir SessionId no Endpoint de Stream

**Arquivo:** `src/Discovery.Api/Controllers/AgentAuthController.cs` — `ChatStream`

**Ações:**
1. Passar `cmd.SessionId` para `StreamAsync` em vez de `null`:
   ```csharp
   await foreach (var chunk in _aiChat.StreamAsync(agentId, cmd.Message, cmd.SessionId, cmd.DepartmentId, ct))
   ```
2. Garantir que o `ChatAsyncCommand` inclua `SessionId` (já existe no record, mas o agent precisa enviá-lo).
3. No agent (`src/internal/ai/chat_stream.go`), confirmar que `SessionID` é enviado no body do request de stream (já existe no `agentChatRequest`, mas verificar se é populado).

#### 2.4 Melhorar System Prompt com Capacidades Reais

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs` — `BuildDefaultSystemPrompt`

**Ações:**
1. Listar explicitamente as ferramentas disponíveis e suas limitações:
   ```
   **Ferramentas disponíveis:**
   - `knowledge_search`: Pesquisa artigos na base de conhecimento da empresa.
   
   **O que você NÃO pode fazer:**
   - NÃO pode executar comandos no computador do usuário (sem shell, sem PowerShell).
   - NÃO pode instalar software diretamente — apenas orientar o usuário.
   - NÃO pode ler arquivos do sistema.
   
   **Se o usuário pedir para executar uma ação:**
   - Oriente-o com passos manuais claros.
   - Se a ação envolver a Loja Integrada do agent, explique como acessá-la via interface.
   ```
2. Remover a linha ambígua "Se precisar executar ferramentas (comandos, scripts), informe claramente antes".
3. Adicionar instrução: "Se `knowledge_search` retornar sem resultados, NÃO chame novamente — responda com seu conhecimento próprio."

---

### Fase 2 — Otimizações (Média Prioridade)

#### 2.5 Otimizar Loop de Tool Calls

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs`

**Ações:**
1. **Deduplicar chamadas `knowledge_search`**: se a mesma query foi chamada e retornou `found:false`, não permitir nova chamada com a mesma query na mesma sessão.
   ```csharp
   var executedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   // Antes de executar knowledge_search:
   if (toolCall.Name == "knowledge_search")
   {
       var query = ExtractQuery(toolCall.ArgumentsJson);
       if (!executedQueries.Add(query))
       {
           // Pular execução, retornar cache
           yield return new AiChatStreamChunk(Type: "tool_result", ...,
               ToolResult: "{\"found\":false,\"message\":\"Busca já realizada sem resultados.\"}");
           continue;
       }
   }
   ```
2. **Reduzir `MaxToolCallIterations` padrão** de 3 para 2 (a 3ª iteração raramente adiciona valor).
3. **Break early** se `knowledge_search` retornar vazio 2x seguidas — forçar resposta direta.

#### 2.6 Corrigir XML Fallback com Follow-up LLM

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs` — `ParseAndExecuteXmlToolCallsAsync`

**Ações:**
1. Mover a execução de XML tool calls para **dentro** do loop de iteração (antes do break).
2. Após executar XML tools, fazer nova chamada LLM com os resultados:
   ```csharp
   if (xmlToolResults.Count > 0 && toolIterations < maxIterations - 1)
   {
       // Nova chamada LLM com resultados das XML tools
       await foreach (var token in _llmProvider.StreamWithToolsAsync(...))
       {
           contentBuilder.Append(token);
           yield return new AiChatStreamChunk(Type: "token", Content: token);
       }
   }
   ```
3. Alternativa mais simples: desabilitar o XML fallback para modelos que suportam function calling nativo (gpt-oss-20b suporta).

#### 2.7 Registrar Modelo e Métricas por Request

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs`

**Ações:**
1. Já existe `model_version` na tabela — garantir que seja sempre preenchido com o modelo real retornado pelo provider (não hardcoded).
2. Adicionar campo `quality_score` ou `feedback` opcional na tabela `ai_chat_messages` para o usuário avaliar respostas.
3. Logar no `ChatLogger` do agent o modelo usado (vir do response do API, não só do config).

#### 2.8 Ajustar `MinSimilarityScore` e Verificar KB

**Ações:**
1. No banco, verificar se há artigos na `knowledge_articles` com embeddings gerados:
   ```sql
   SELECT COUNT(*) FROM knowledge_chunks;
   SELECT COUNT(*) FROM knowledge_articles WHERE status='published';
   ```
2. Se vazio, popular a KB com artigos básicos (instalação de software comum, procedimentos de TI).
3. Reduzir `MinSimilarityScore` de 0.65 para 0.55 (modelos de embedding menores têm scores mais baixos).
4. Considerar usar `text-embedding-3-small` (OpenAI) em vez do modelo do OpenRouter para embeddings (mais confiável).

---

### Fase 3 — Arquitetura: Agent MCP Tools no Chat (Server-Managed Agent Loop)

#### 2.9 Visão Geral — Server-Managed Agent Loop

**Problema:** A IA do chat roda 100% server-side. O agent tem ~30 tools MCP locais (install_package, get_inventory, list_printers, etc.) que **não são acessíveis** à IA do chat.

**Decisão de arquitetura:** Após análise de 3 alternativas (Tool Proxy via NATS, SSE+POST callback, Agent-side loop), a abordagem escolhida é **Server-Managed Agent Loop** — o servidor mantém controle total (histórico, system prompt, cost control, auditoria, policies) e o agent executa as tools localmente em rounds múltiplos.

**Por que não NATS:** Adiciona dependência de infraestrutura e complexidade de timeout/retry desnecessária quando o HTTP/SSE já atende.

**Por que não SSE+POST callback:** Mantém conexão SSE aberta durante execução da tool (risco de timeout), exige estado em memória (`TaskCompletionSource` por callId), não sobrevive a restart do servidor, e tem problemas com load balancer.

**Vantagens do Server-Managed Agent Loop:**
- ✅ Servidor mantém **controle total**: system prompt, histórico, cost control, rate limit, guardrails, auditoria
- ✅ Agent executa tools localmente via `McpToolExecutor` já implementado (~30 tools)
- ✅ **Stateless entre rounds** — cada round é uma chamada HTTP independente
- ✅ Stream SSE fecha entre rounds — **nenhuma conexão aberta** durante execução de tools
- ✅ Sobrevive a restart do servidor (estado no DB, não em memória)
- ✅ Compatível com load balancer (cada round pode cair em instância diferente)
- ✅ Revisão de conversas: todas as mensagens (user, assistant, tool) persistidas no DB
- ✅ Sem infra nova — apenas HTTP, que já existe

#### 2.10 Arquitetura Detalhada

```
┌─────────────────────────────────────────────────────────────┐
│  SERVIDOR (mantém controle total)                           │
│                                                             │
│  ✓ System prompt (server-side, por scope)                  │
│  ✓ Histórico persistido no DB (ai_chat_messages)            │
│  ✓ Cost control / rate limit / token budget                 │
│  ✓ Tool policies (quais tools o agent pode usar)            │
│  ✓ Auditoria / revisão de conversas                         │
│  ✓ Guardrails (PII/secret detection)                        │
│  ✗ NÃO executa tools (apenas define schemas)                │
└─────────────────────────────────────────────────────────────┘
          ▲                                    │
          │ HTTP/SSE (chamadas curtas)         │
          │                                    ▼
┌─────────────────────────────────────────────────────────────┐
│  AGENT (executa tools localmente)                           │
│                                                             │
│  ✓ Recebe tool_calls do LLM via SSE                         │
│  ✓ Executa via McpToolExecutor local (30 tools)             │
│  ✓ POSTa resultados de volta                               │
│  ✓ Renderiza resposta para o usuário                        │
│  ✗ NÃO controla system prompt nem histórico                 │
└─────────────────────────────────────────────────────────────┘
```

#### 2.11 Fluxo Multi-Round

**Round 1 — Usuário envia mensagem:**

```
Agent → POST /me/ai-chat/stream
         {message: "instalar firefox", sessionId: "abc"}

Server:
  1. Valida agent auth + rate limit + cost budget
  2. Carrega histórico da sessão "abc" do DB
  3. Injeta system prompt (server-controlled)
  4. Monta lista de tools permitidas (server policy → schemas do agent + knowledge_search)
  5. Chama LLM com {system, history, user_msg, tools[]}
  6. Persiste mensagem do user no DB

Server → SSE:
  data: {"type":"token","content":"Vou verificar"}
  data: {"type":"token","content":"os pacotes..."}
  data: {"type":"tool_call","callId":"1","name":"search_packages","args":{"query":"firefox"}}
  data: {"type":"round_end","sessionId":"abc"}
  ── stream fecha ──

Agent: recebe tokens (renderiza), recebe tool_call
```

**Round 2 — Agent executa tool e envia resultado:**

```
Agent: executa search_packages("firefox") via McpToolExecutor local
       → resultado: {"packages":[{"id":"Mozilla.Firefox","name":"Firefox",...}]}

Agent → POST /me/ai-chat/stream
         {sessionId: "abc", toolResults: [{"callId":"1","result":"{...}"}]}

Server:
  1. Carrega histórico (inclui Round 1)
  2. Persiste tool_result no DB (role: "tool")
  3. Chama LLM com contexto atualizado
  4. Persiste resposta do assistant

Server → SSE:
  data: {"type":"token","content":"Encontrei o Firefox"}
  data: {"type":"token","content":"Posso instalar?"}
  data: {"type":"tool_call","callId":"2","name":"install_package","args":{"id":"Mozilla.Firefox"}}
  data: {"type":"round_end","sessionId":"abc"}
```

**Round 3 — Agent instala e confirma:**

```
Agent: executa install_package("Mozilla.Firefox") via winget
       → resultado: {"success":true,"message":"Firefox instalado"}

Agent → POST /me/ai-chat/stream
         {sessionId: "abc", toolResults: [{"callId":"2","result":"{...}"}]}

Server → SSE:
  data: {"type":"token","content":"Firefox instalado com sucesso!"}
  data: {"type":"done","sessionId":"abc","tokensUsed":4500}
```

#### 2.12 Comparação com Alternativas Rejeitadas

| Critério | NATS Proxy | SSE+POST Callback | **Server-Managed Loop (escolhido)** |
|----------|-----------|-------------------|-------------------------------------|
| Infra necessária | NATS | Nenhuma | **Nenhuma** |
| Conexão durante tool exec | NATS request-reply | SSE aberta (risco timeout) | **Stream fecha** |
| Estado em memória | Sim (request-reply) | `TaskCompletionSource` por callId | **Nenhum** |
| Sobrevive a restart do server | Parcial | Não (perde callbacks) | **Sim (estado no DB)** |
| Load balancer | Problemático | POST pode cair em instância errada | **Cada round é independente** |
| Controle de cost/limites | Server | Server | **Server** |
| Auditoria | Server | Server | **Server (persiste tudo)** |
| Complexidade | Alta | Média | **Média** |

#### 2.13 Implementação — Servidor (API)

**Endpoint modificado:** `POST /me/ai-chat/stream`

```csharp
public record ChatStreamCommand(
    Guid AgentId,
    string? Message,                    // null em rounds de tool_result
    string? SessionId,                  // null = criar nova sessão
    List<ToolResultDto>? ToolResults,   // preenchido em rounds 2+
    Guid? DepartmentId,
    string? ClientIp);

public record ToolResultDto(string CallId, string Result);
```

**Mudanças no `AiChatService.StreamAsync`:**
1. Se `ToolResults` não for vazio: carregar histórico, adicionar tool_results como mensagens `role: "tool"`, chamar LLM
2. Se `Message` não for null: nova mensagem de user, persistir no DB
3. Em ambos os casos: persistir no DB, injetar system prompt, validar policies
4. Tools enviadas ao LLM = schemas das tools do agent (registradas via endpoint) + `knowledge_search` (server-side)
5. Quando LLM retorna tool_calls: emitir `tool_call` events via SSE, depois `round_end` e fechar stream
6. Quando LLM retorna texto final (sem tool_calls): emitir tokens, depois `done`

**Novo endpoint:** `POST /me/agent-tools/registry` — agent registra suas tools no startup

```csharp
public record RegisterAgentToolsCommand(
    Guid AgentId,
    List<AgentToolDto> Tools);

public record AgentToolDto(
    string Name,
    string Description,
    JsonElement ParametersSchema);
```

O server valida contra `mcp_tool_policies` (quais tools são permitidas para aquele scope) e armazena em cache (memória com TTL de 5 min ou Redis).

**Novos tipos de chunk SSE:**

```csharp
// Adicionar ao AiChatStreamChunk:
// Type = "tool_call"     → ToolCallId + ToolName + ToolArgumentsDelta (args JSON completo)
// Type = "round_end"     → SessionId preenchido (fecha stream, agent deve fazer novo POST)
```

**Mudanças no `McpToolExecutor`:**
1. `GetAvailableToolsAsync` agora retorna também as tools do agent registradas (validadas contra policy)
2. `ExecuteAsync` para tools do agent: **não executa** — retorna erro indicando que a tool deve ser executada pelo agent (o loop no `StreamAsync` detecta isso e emite `tool_call` via SSE em vez de executar)

**Segurança — Tool Policies por Scope:**

```sql
-- Tools permitidas por scope (exemplo)
-- Read-only (sempre permitidas):
INSERT INTO mcp_tool_policies (tool_name, is_enabled, scope) VALUES
  ('search_packages', true, 'agent'),
  ('list_installed_packages', true, 'agent'),
  ('get_pending_updates', true, 'agent'),
  ('list_printers', true, 'agent'),
  ('get_performance_snapshot', true, 'agent'),
  ('get_inventory', true, 'agent'),
  ('get_top_processes', true, 'agent'),
  ('get_disk_health', true, 'agent'),
  ('query_event_log', true, 'agent'),
  ('get_recent_errors', true, 'agent');

-- Ações (requerem confirmação do usuário na UI do agent):
INSERT INTO mcp_tool_policies (tool_name, is_enabled, scope) VALUES
  ('install_package', true, 'agent'),
  ('uninstall_package', true, 'agent'),
  ('upgrade_package', true, 'agent');

-- Bloqueadas por padrão:
INSERT INTO mcp_tool_policies (tool_name, is_enabled, scope) VALUES
  ('flush_dns', false, 'agent'),
  ('restart_spooler', false, 'agent'),
  ('clear_queue', false, 'agent');
```

#### 2.14 Implementação — Agent

**Mudanças no `ai.Service.SendStream` (`src/internal/ai/chat_stream.go`):**

```go
func (s *Service) SendStream(ctx context.Context, message string, onToken func(string)) error {
    // Round 1: envia mensagem do usuário
    resp, err := s.callAgentChatStream(ctx, &agentChatRequest{
        Message:   message,
        SessionID: s.sessionID,
    })
    
    var pendingToolCalls []toolCall
    
    for {
        // Processa SSE events
        for event := range resp.Events {
            switch event.Type {
            case "token":
                onToken(event.Content)
            case "tool_call":
                pendingToolCalls = append(pendingToolCalls, toolCall{
                    CallID: event.ToolCallID,
                    Name:   event.ToolName,
                    Args:   event.ToolArguments,
                })
            case "round_end":
                s.sessionID = event.SessionID
                // Stream fechou — sair do loop de eventos
                break
            case "done":
                s.sessionID = event.SessionID
                return nil
            }
        }
        
        // Se há tool_calls pendentes, executar e fazer próximo round
        if len(pendingToolCalls) == 0 {
            break
        }
        
        // Executar tools localmente via McpToolExecutor
        var results []toolResult
        for _, tc := range pendingToolCalls {
            result, err := s.mcpExecutor.Execute(ctx, tc.Name, tc.Args)
            results = append(results, toolResult{
                CallID: tc.CallID,
                Result: result,
            })
        }
        pendingToolCalls = nil
        
        // Round 2+: envia resultados das tools
        resp, err = s.callAgentChatStream(ctx, &agentChatRequest{
            SessionID:   s.sessionID,
            ToolResults: results,
        })
    }
    
    return nil
}
```

**Mudanças no SSE parser do agent:**
- Adicionar handling para `type: "tool_call"` (hoje só trata `token`, `done`, `error`)
- Adicionar handling para `type: "round_end"` (fecha stream, prepara próximo round)
- Adicionar campos na struct `agentChatStreamEvent`:
  ```go
  type agentChatStreamEvent struct {
      Type             string `json:"type"`
      Content          string `json:"content"`
      SessionID        string `json:"sessionId"`
      Error            string `json:"error"`
      LatencyMs        int    `json:"latencyMs"`
      ToolCallID       string `json:"toolCallId"`       // NOVO
      ToolName         string `json:"toolName"`          // NOVO
      ToolArguments    string `json:"toolArguments"`     // NOVO (JSON completo)
  }
  ```

**Registro de tools no startup (`src/app/app.go`):**
- Após inicialização, o agent coleta a lista de tools do `McpToolExecutor` local
- Envia `POST /me/agent-tools/registry` com nome, descrição e schema de cada tool
- Repete a cada 5 min (TTL do cache no servidor) ou quando tools mudam

#### 2.15 Unificar Parser SSE no Agent

**Arquivo:** `src/app/chat_bridge.go`

**Ações:**
1. Refatorar `chat_bridge.go` para usar o mesmo parser JSON de `ai/chat_stream.go`.
2. Ou remover `chat_bridge.go` se `ai.Service` já cobre todos os casos (após implementação do multi-round).

#### 2.16 Implementar Tools Server-Side Read-Only

**Arquivo:** `src/Discovery.Infrastructure/Services/McpToolExecutor.cs`

**Ações:**
1. Implementar handler para `time.current` (trivial — retorna `DateTime.UtcNow`).
2. Implementar handler para `sequential_thinking` (no-op — apenas registra o pensamento para trace).
3. Implementar handler para `memory.search` (consulta tabela `ai_chat_messages` da sessão).
4. Remover ou desabilitar policies de `filesystem.read_file` e `postgres.query` se não houver intenção de implementar (segurança).

#### 2.17 Adicionar Cache de Respostas Comuns

**Arquivo:** `src/Discovery.Infrastructure/Services/AiChatService.cs`

**Ações:**
1. Para mensagens muito curtas e comuns ("oi", "olá", "teste"), responder instantaneamente sem chamar o LLM:
   ```csharp
   private static readonly Dictionary<string, string> QuickReplies = new(StringComparer.OrdinalIgnoreCase)
   {
       ["oi"] = "Olá! Como posso ajudar você hoje?",
       ["olá"] = "Olá! Em que posso ajudar?",
       ["teste"] = "Olá! Como posso ajudar você hoje?",
   };
   ```
2. Verificar antes de chamar o LLM (apenas se não houver histórico na sessão).

---

## 3. Priorização e Esforço

### Fase 1 — Correções Críticas (Alta Prioridade)

| # | Item | Severidade | Esforço |
|---|------|-----------|---------|
| 2.1 | Corrigir Encoding UTF-8 | Alta | Baixo (2h) |
| 2.2 | Tratar Respostas Vazias | Alta | Baixo (2h) |
| 2.3 | Corrigir SessionId no Stream | Alta | Baixo (1h) |
| 2.4 | Melhorar System Prompt | Alta | Baixo (1h) |

### Fase 2 — Otimizações (Média Prioridade)

| # | Item | Severidade | Esforço |
|---|------|-----------|---------|
| 2.5 | Otimizar Loop de Tool Calls | Média | Médio (4h) |
| 2.6 | Corrigir XML Fallback | Média | Médio (4h) |
| 2.7 | Registrar Modelo e Métricas | Média | Baixo (2h) |
| 2.8 | Ajustar KB e Similarity | Média | Baixo (2h) |

### Fase 3 — Arquitetura: Agent MCP Tools no Chat

| # | Item | Severidade | Esforço |
|---|------|-----------|---------|
| 2.9 | Server-Managed Agent Loop (visão geral) | Alta | — |
| 2.10 | Arquitetura detalhada | Alta | — |
| 2.11 | Fluxo multi-round | Alta | — |
| 2.12 | Comparação com alternativas | — | — |
| 2.13 | Implementação servidor (API) | Alta | Médio (9h) |
| 2.14 | Implementação agent | Alta | Médio (8h) |
| 2.15 | Unificar Parser SSE | Baixa | Baixo (1h) |
| 2.16 | Implementar Tools Server-Side Read-Only | Baixa | Médio (4h) |
| 2.17 | Cache de Respostas Comuns | Baixa | Baixo (1h) |

**Total Fase 3:** ~23h (3 dias)

---

## 4. Métricas de Validação

Após implementação, validar com:

1. **Encoding:** Enviar mensagem com acentos e emojis — resposta deve preservar caracteres.
2. **Resposta vazia:** Reproduzir cenário com KB vazia — IA deve responder com conhecimento próprio.
3. **Sessão:** Enviar 2 mensagens seguidas — a 2ª deve ter contexto da 1ª.
4. **Latência:** "oi" deve responder em <3s (com cache); mensagens com tools em <15s.
5. **Tool alucinada:** IA não deve mais tentar chamar `shell`.
6. **KB:** Após popular, `knowledge_search` deve retornar resultados relevantes.
7. **Agent tools (Fase 3):** IA deve conseguir chamar `search_packages` e `list_installed_packages` via multi-round.
8. **Persistência (Fase 3):** Tool results devem aparecer no DB como `role: "tool"` com `tool_name` e `tool_arguments_json`.
9. **Auditoria (Fase 3):** Deve ser possível revisar conversa completa (user → tool_call → tool_result → assistant) via query no DB.
10. **Cost control (Fase 3):** Server deve contar tokens de todos os rounds e bloquear se exceder budget.

---

## 5. Notas sobre Modelo (gpt-oss-20b)

O usuário reportou que `openai/gpt-oss-20b` (via OpenRouter) teve "bom desempenho". Observações:

- **Latência:** Variável (1.4s a 33s) — típico de modelos open-source em infra compartilhada.
- **Function calling:** Suporta nativamente, mas ocasionalmente alucina tools inexistentes (corrigido com system prompt melhorado).
- **Qualidade:** Respostas detalhadas e bem estruturadas, mas tende a ser verboso (8k-33k tokens por resposta).
- **Recomendação:** Manter como modelo padrão para chat, mas:
  - Reduzir `MaxTokensPerRequest` de 2000 para 1500 (respostas mais concisas).
  - Considerar `temperature: 0.5` (mais determinístico, menos verboso).
  - Para embeddings, usar `text-embedding-3-small` da OpenAI diretamente (não via OpenRouter).
