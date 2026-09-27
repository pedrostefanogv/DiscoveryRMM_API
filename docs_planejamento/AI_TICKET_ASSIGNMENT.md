# Triagem por IA na Auto-atribuição de Chamados

> Implementado em: migração M179 (20261001_179). Escopo: API (DiscoveryRMM_API) e console (DiscoveryRMM_Site).
>
> Fase 3 (segunda evolução): orçamento de tokens por modelo, cost control, aprendizado híbrido e
> auto-atribuição de chamados de alerta (M180); processamento periódico configurável das métricas e da
> triagem, com configuração global herdada pelo cliente (M181) — ver BACKGROUND_PROCESSING_SETTINGS.md.

## 1. Visão geral

O departamento passa a ter a estratégia de auto-atribuição **Triagem por IA**
(assignment_strategy = 3). Nela, o responsável do chamado é escolhido cruzando o
conteúdo e a dificuldade do chamado com:

- métricas históricas de cada atendente (carga, tempo de resolução, primeira resposta, SLA, reincidência, CSAT);
- afinidade com chamados semelhantes já resolvidos (similaridade textual, pg_trgm);
- competências cadastradas no perfil do membro (tags + nível), teto de chamados e peso do gestor.

A rodada é **assíncrona** (o request de abertura nunca chama o provedor de IA) e
toda decisão fica registrada e auditável, com candidatos, score, confiança, modelo e
justificativa. Qualquer falha cai em um fallback determinístico configurável.

## 2. Como configurar

1. No cadastro do departamento, em **Auto-atribuição de chamados**, selecione
   **Triagem por IA (métricas da equipe)** e salve.
2. Ajuste o bloco **Triagem por IA**:
   - Como aplicar a decisão: Sugerir (humano confirma) ou Atribuir automaticamente;
   - Fallback quando a IA não decide: round-robin ou menos chamados abertos;
   - Confiança mínima (0 a 1, default 0.60);
   - Máximo de candidatos enviados ao modelo (default 8);
   - Usar afinidade com chamados semelhantes;
   - Orientações livres do gestor (anexadas ao prompt);
   - Pesos do score (default: competência 0.25, afinidade 0.25, performance 0.20, carga 0.15, CSAT 0.10, qualidade de SLA 0.05).
3. Na aba **Equipe**, preencha competências, nível (1 a 5), teto de chamados abertos,
   peso e o opt-out **Aceita ser escolhido automaticamente pela IA**.

Pré-requisito: integração de IA habilitada (AIIntegration.Enabled + ChatAIEnabled +
API key). Sem isso a triagem apenas registra ai_unavailable e aplica o fallback.

## 3. Fórmula do score determinístico

    score = skill + afinidade + performance + carga + csat + sla_quality
            (pesos normalizados para somar 1) * peso do gestor - penalidade de capacidade

- skill: sobreposição entre tags/categoria do chamado e as competências do membro
  (exato 1.0, parcial 0.5, nenhum 0.1), multiplicada pelo fator de nível (0.6 a 1.0).
- afinidade: maior similaridade (pg_trgm) com chamados já resolvidos pelo membro. O índice GIN
  cobre a expressão título + descrição usada na consulta (ix_tickets_search_text_trgm).
- performance: 0.60 * velocidade (relativa à mediana do departamento) + 0.25 * (1 - reincidência)
  + 0.15 * ajuste de dificuldade.
- carga: 1 - abertos / maior quantidade de abertos do departamento.
- csat: média / 5, neutro 0.5 quando há menos de 3 avaliações.
- sla_quality: 1 - taxa de SLA violado.
- penalidade de capacidade: -0.15 quando o membro já atingiu o teto de chamados abertos.

Desempate estável: maior score, menos abertos, UserId (ordem reproduzível).

## 4. Contrato com o modelo

O modelo recebe o chamado (título, descrição truncada em 4000 caracteres, categoria,
prioridade, dificuldade heurística, tags), a tabela de candidatos com o score calculado
e as orientações do gestor. Deve responder **somente** JSON:

    {
      "chosenUserId": "<uuid de um candidato>",
      "confidence": 0.0,
      "difficulty": 4,
      "tags": ["vpn"],
      "rationale": "1 a 3 frases",
      "alternateUserId": null
    }

A resposta é validada: usuário precisa estar entre os candidatos. JSON inválido, usuário
desconhecido ou confiança abaixo do mínimo fazem a triagem usar o maior score
(strategy_source = fallback_score), registrando a confiança original.

Privacidade: campos internos de custom fields e respostas sensíveis de template nunca
são enviados ao provedor.

## 5. Persistência e auditoria

- technician_metrics_snapshots: métricas materializadas por atendente (TTL de 15 min, recalculadas por job a cada 15 min). Tempos e taxas usam a janela de 90 dias por data de criação; a carga atual considera todos os chamados abertos.
- ticket_assignment_decisions: decisão com candidatos (JSON), score, confiança, dificuldade, modelo, tokens, applied e motivo de não aplicação.
- ai_assignment_queue: fila (uma linha por chamado) com retry exponencial (máximo 3 tentativas) e status. Itens concluídos/ignorados com mais de 30 dias são purgados pelo DataRetentionJob.
- TicketActivityLog: AiAssigned (aplicada) e AiAssignmentSuggested (sugerida).

## 6. Fluxo

    criação do chamado -> estratégia AiTriage -> fila (ai_assignment_queue)
      -> job (20 s) -> métricas + afinidade -> score -> IA (JSON) -> validação
      -> decisão persistida -> atribuição (modo automático) + atividade + notificação

Rede de segurança: o mesmo job varre chamados com estratégia AiTriage **em modo
automático** sem responsável após 5 minutos (RetryAfterMinutes) e aplica o fallback
determinístico. O enfileiramento também é coberto por essa varredura caso falhe. No modo
assistido o chamado permanece sem responsável até a confirmação humana (a varredura não
interfere).

## 7. Endpoints

Departamentos (RequirePermission em Departments):

- GET  /api/v1/departments/{id}/assignment/team-metrics
- PUT  /api/v1/departments/{id}/members/{userId}/profile
- POST /api/v1/departments/{id}/assignment/metrics/refresh

Chamados (RequirePermission em Tickets):

- GET  /api/v1/tickets/assignment/{ticketId}/decision
- POST /api/v1/tickets/assignment/{ticketId}/preview (consultivo: registra a decisão, sem atividade no histórico nem notificação)
- POST /api/v1/tickets/assignment/{ticketId}/apply
- GET  /api/v1/tickets/assignment/decisions

## 8. Configuração de background

    BackgroundJobs:AiTicketAssignment:Enabled (default true)
    BackgroundJobs:AiTicketAssignment:IntervalSeconds (default 20, piso 10)
    BackgroundJobs:AiTicketAssignment:StartupDelaySeconds (default 20)
    BackgroundJobs:AiTicketAssignment:BatchSize (default 25)
    BackgroundJobs:AiTicketAssignment:RetryAfterMinutes (default 5)
    BackgroundJobs:TechnicianMetrics:Enabled (default true)
    BackgroundJobs:TechnicianMetrics:IntervalMinutes (default 15, piso 5)
    BackgroundJobs:TechnicianMetrics:StartupDelaySeconds (default 60)

## 9. Modos de falha

| Cenário | Comportamento |
|---|---|
| IA desabilitada/sem chave | strategy_source = ai_unavailable. No modo automático aplica a estratégia de fallback configurada; no modo assistido sugere o maior score (sem mover o cursor do round-robin) |
| Timeout/erro do provedor | 3 tentativas com backoff; depois fallback determinístico |
| JSON inválido ou usuário desconhecido | maior score (fallback_score) |
| Confiança abaixo do mínimo | maior score |
| Sem membros elegíveis | sem atribuição + no_candidates |
| Todos com opt-out | sem atribuição + no_candidates |
| Membro no teto de chamados | entra com penalidade de 0.15; se a IA escolher alguém no teto, uma alternativa livre com score equivalente é usada |
| Atribuição manual concorrente | job não sobrescreve (already_assigned) |
| pg_trgm indisponível | afinidade = 0, restante do score continua |
| Chamado sem site (agent/alerta) | usa um site do mesmo cliente para resolver as configurações de IA |
| Chamado de cliente sem nenhum site | ai_unavailable + fallback |

## 10. Custo e observabilidade

- Uma chamada de LLM por chamado, teto de 700 tokens de saída, temperatura 0.2, response_format json_object;
  tokens e modelo gravados na decisão.
- Contadores por origem da decisão (ai, fallback_score, fallback_strategy, ai_unavailable, no_candidates, error).
- Rollback: voltar a estratégia do departamento para 0, 1 ou 2; a migração é aditiva e os jobs podem ser desligados.

## 11. Evolução aplicada (segunda fase)

Itens antes registrados como limitação e agora implementados: orçamento de tokens por modelo,
cost control nos fluxos de IA de chamado, aprendizado híbrido (competências + pesos) e
auto-atribuição dos chamados criados por alerta. A herança de departamento segue NÃO
implementada, por decisão de produto — apenas o ponto de extensão foi preparado.

Continua em aberto (fases futuras):

- Extração automática de competências a partir de embeddings de título/descrição (hoje a
  extração usa categorias + tokens relevantes; não usa vetores).
- Herança de equipe via InheritFromGlobalId (ver DEPARTMENT_INHERITANCE_SEAM.md).
- Recalibração usando correlação estatística além da taxa de override por dimensão.

---

## 13. Orçamento de tokens por modelo (segunda fase)

O teto de saída era fixo (8000 no clamp de configuração e 700 na triagem), muito abaixo da
capacidade real dos modelos contratados. Agora:

- `AITokenLimits` (função pura) resolve `min(pedido, teto do departamento, capacidade do modelo, teto do produto)`;
- capacidade vem do catálogo (`max_completion_tokens` do OpenRouter) com fallback por família
  de modelo quando o catálogo não informa (ex.: OpenAI nativo, que passou a ser preenchido por
  `GetOpenAiMaxCompletionTokens`);
- `AITokenBudgetResolver` compõe o orçamento por site (cache de 30 min por modelo) e é usado
  pela triagem; o teto de configuração agora aceita até 32.768 (antes 8000, o que revertia
  silenciosamente a configuração para o default);
- o truncamento do prompt deixou de ser fixo em 4000 caracteres e passa a derivar da janela de
  contexto do modelo (90% do contexto menos a saída reservada, com margem de segurança).

Precedência de limites (de fora para dentro): teto do produto (32.768) > `MaxTokensPerRequest`
configurado no tenant > capacidade real do modelo > teto do departamento. Ou seja, o teto do
departamento NUNCA ultrapassa a configuração do tenant — para usar 16.000 na triagem, o
`MaxTokensPerRequest` do tenant também precisa permitir.

Modelo desconhecido é cacheado por 5 minutos (e modelo válido por 30): corrigir o id na
configuração tem efeito em minutos, não no TTL cheio.

**Automação para todos os fluxos de IA de chamado.** O orçamento model-aware passou a ser
resolvido dentro do `AiChatService` (`ProcessTicketPromptAsync` / `ProcessTicketPromptJsonAsync`),
então qualquer fluxo de ticket presente ou futuro já respeita a capacidade do modelo sem código
extra. Os handlers de ticket (triagem textual, resumo, resposta sugerida e artigo de KB) ganharam
também:

- truncamento do prompt pelo orçamento (`MaxPromptChars`) — descrições longas não estouram mais a
  janela de contexto do provedor;
- site efetivo do mesmo cliente quando o chamado não tem `SiteId` (antes esses fluxos falhavam com
  "Site '' not found");
- **uma única** leitura do chamado por ação de IA (antes cada handler lia duas vezes — menos um
  round trip por resumo/resposta/artigo);
- mensagem específica quando o cost control bloqueia ("Limite de uso de IA atingido"), em vez de
  erro genérico.

**Chat do agente também adota o orçamento model-aware.** O `AiChatStreamingOrchestrator`
(`StreamAsync` e `StreamMultiRoundAsync`, incluindo as sínteses forçadas sem tools) e o caminho
síncrono (`AiChatService.ProcessSyncAsync`) resolvem o teto efetivo com o mesmo resolvedor:

- o valor configurado no tenant continua sendo o "pedido", mas nunca ultrapassa a capacidade real
  do modelo nem o teto do produto;
- falha do resolvedor **não derruba o chat**: mantém o teto do tenant (log em debug);
- no caminho síncrono, o `requestMaxTokens` explícito do cliente deixou de ser limitado por um clamp
  fixo de 8000 (que rebaixava pedidos legítimos).

Limites verificados no catálogo do OpenRouter (26/09/2026):

| Modelo | Contexto | Saída máxima |
|---|---|---|
| z-ai/glm-5.3 | até 1.048.576 | 65.536 a 262.144 |
| z-ai/glm-5.3-flash | até 1.048.576 | 131.072 a 943.718 |
| xiaomi/mimo-v2.6-flash | 1.048.576 | 131.072 |
| deepseek/deepseek-v4.1-flash | ~1.048.576 | 32.768 a 393.216 |
| openai/gpt-4o-mini e gpt-4o | 128.000 | 16.384 |
| google/gemini-2.5-flash | 1.048.576 | 65.535 |
| openai/o3-mini e o4-mini | 200.000 | 100.000 |

Observações operacionais: `deepseek/deepseek-flash-latest` não existe no catálogo (404) e
`deepseek/deepseek-pro-latest` não pôde ser verificado; modelos desconhecidos usam o teto
conservador de 4.096 e a decisão registra a origem do orçamento em `max_output_tokens`.

---

## 14. Cost control nos fluxos de IA de chamado

`ProcessTicketPromptAsync` e `ProcessTicketPromptJsonAsync` passam a respeitar
`IAiCostControlService` (rate limit por minuto e budget diário de tokens, por cliente+site):

- `TryAcquireAsync` antes da chamada; negado => `AiUsageLimitException` (motivo `rate_limit_or_budget`);
- `RecordUsageAsync` com os tokens reais após o sucesso;
- `ReleaseAsync` quando a chamada falha antes de consumir o LLM.

Na triagem, o bloqueio não é erro: vira `strategy_source = ai_budget_exceeded`, com fallback
determinístico e motivo auditável (exibido no console como "Limite de uso de IA"). Só tem efeito
quando `CostControlEnabled` está ligado.

Ordem das verificações: o budget diário é checado ANTES do rate limit. Antes, uma requisição
bloqueada por budget consumia um slot de rate limit (o usuário era punido duas vezes por uma
chamada que nunca chegou ao provedor).

---

## 15. Aprendizado híbrido (competências e pesos)

Configurável por departamento: Off / Sugerir / Aplicar automaticamente.

- Competências: `SkillExtractor` agrega os chamados resolvidos do atendente (janela de 90 dias),
  conta categorias e tokens relevantes e sugere apenas tags com evidência mínima
  (`ai_skill_min_evidence`), até `ai_skill_max_tags`. Tags manuais são preservadas; `SkillLevel`
  nunca é alterado automaticamente.
- Pesos: `WeightCalibrator` usa a taxa de override por dimensão (overrides quando o escolhido
  estava acima vs abaixo da mediana do pool). Dimensão supervalorizada reduz peso; subvalorizada
  aumenta. Salvaguardas: mínimo de 20 decisões aplicadas, mínimo de 3 amostras por lado, força
  mínima de 5 pontos percentuais, delta máximo por ciclo (`ai_weight_max_delta_per_cycle`),
  faixa `ai_weight_min`..`ai_weight_max` e soma renormalizada para 1,0.
- O override é registrado quando um humano troca o responsável após decisão aplicada
  (`overridden_at`/`overridden_by_user_id`) — inclusive no endpoint de assign.
- Job `AiAssignmentLearningJob` (diário, hora configurável) roda o ciclo; endpoints:
  `GET /departments/{id}/learning/suggestions`,
  `POST /departments/{id}/learning/run`,
  `POST /departments/{id}/learning/skills/{suggestionId}/apply|discard`,
  `POST /departments/{id}/learning/weights/{suggestionId}/apply|discard`.
- Sugestões pendentes anteriores do mesmo alvo são substituídas a cada ciclo (sem acúmulo).
- Aplicações automáticas geram **auditoria de configuração** (`ConfigurationAudit`) com o valor
  anterior e o novo, motivo `ai_learning_auto` e autor `ai-assignment-learning` — mudança de
  configuração feita pela IA sem humano fica rastreável.
- O card do console mostra a evidência: para competências, contagem por tag; para pesos, taxa de
  troca por dimensão (trocas/amostras, força e delta).
- `technician_metrics_snapshots.difficulty_average` passou a ser preenchido a partir das decisões
  aplicadas da IA — antes a dimensão de dificuldade do score era permanentemente neutra.

---

## 16. Chamados criados por alerta/evento

`AlertToTicketService` passou a usar o MESMO serviço de auto-atribuição dos demais chamados
(`ITicketAutoAssignmentService`): responsável explícito da regra vence; senão aplica round-robin /
menos abertos ou enfileira a triagem por IA. Sem departamento, permanece sem responsável.
Falha na auto-atribuição é registrada em log e não impede a criação do chamado.

---

## 17. Ponto de extensão da herança de departamento

`IDepartmentTeamResolver` é a única fonte da equipe candidata, usada tanto pela estratégia
determinística quanto pela triagem. A herança por `InheritFromGlobalId` NÃO está implementada;
quando for, entra apenas nesse resolvedor. Detalhes em DEPARTMENT_INHERITANCE_SEAM.md.

## 12. Testes

Backend (NUnit): AiAssignmentScorerTests, TicketSignalExtractorTests, TechnicianMetricsServiceTests,
AiTicketTriageServiceTests, TicketAutoAssignmentServiceTests, AiAssignmentLearningServiceTests,
AiCostControlServiceTests, TicketAiBudgetTests, AiTokenBudgetTests, WeightCalibratorTests e
SkillExtractorTests. Frontend (Vitest): TicketAiAssignmentCard.test.tsx e DepartmentLearningCard.test.tsx.
