using System.Diagnostics;
using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Triagem por IA da auto-atribuição de chamados.
///
/// Fluxo: monta os candidatos elegíveis da equipe do departamento, calcula o
/// score determinístico (AssignmentScorer), consulta o modelo quando disponível
/// e decide. A decisão é sempre registrada em ticket_assignment_decisions com os
/// candidatos, score, confiança, modelo e justificativa, e o resultado é
/// aplicado (modo automático) ou apenas sugerido (modo assistido). Qualquer
/// falha cai no fallback determinístico configurado no departamento.
/// </summary>
public class AiTicketTriageService(
    DiscoveryDbContext db,
    IAiAssignmentQueueRepository queueRepository,
    ITechnicianMetricsService metricsService,
    ITechnicianAffinityRepository affinityRepository,
    ITicketDifficultyAssessor difficultyAssessor,
    ITicketAssignmentService assignmentService,
    IAiChatService aiChat,
    IActivityLogService activityLog,
    INotificationService notification,
    IAiTokenBudgetResolver tokenBudgetResolver,
    IDepartmentTeamResolver teamResolver,
    IConfigurationResolver configurationResolver,
    ILogger<AiTicketTriageService> logger) : IAiTicketTriageService
{
    private const double Temperature = 0.2;
    private const int AffinityPoolSize = 30;

    /// <summary>Escopos processados por execução do ciclo (fairness/limite de trabalho).</summary>
    private const int MaxScopesPerRun = 50;

    // Marcador de bloco de código (tres crases) sem literal no fonte.
    private static readonly string Fence = string.Concat(Enumerable.Repeat((char)96, 3));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public async Task<TicketAssignmentResultDto> RecommendAsync(
        Guid ticketId, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var evaluation = await EvaluateAsync(ticketId, ct);
        if (evaluation is null)
            return NotFoundResult(ticketId);

        var reason = evaluation.Department.AiAssignmentMode == (int)AiAssignmentMode.AutoAssign
            ? "pending_auto"
            : "suggest_only";

        return await PersistAsync(evaluation, apply: false, reason, triggeredByUserId, ct);
    }

    public async Task<TicketAssignmentResultDto> PreviewAsync(Guid ticketId, CancellationToken ct = default)
    {
        var evaluation = await EvaluateAsync(ticketId, ct);
        if (evaluation is null)
            return NotFoundResult(ticketId);

        // Sem atividade/notificação: o preview é consultivo e repetível.
        return await PersistAsync(evaluation, apply: false, "preview", null, ct);
    }

    public async Task<TicketAssignmentResultDto> ApplyAsync(
        Guid ticketId, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var evaluation = await EvaluateAsync(ticketId, ct);
        if (evaluation is null)
            return NotFoundResult(ticketId);

        var ticket = evaluation.Ticket;
        if (ticket.AssignedToUserId.HasValue)
            return await PersistAsync(evaluation, apply: false, "already_assigned", triggeredByUserId, ct);

        if (evaluation.ChosenUserId is null)
            return await PersistAsync(evaluation, apply: false, "no_candidates", triggeredByUserId, ct);

        // IA indisponível: a atribuição automática respeita a estratégia de
        // fallback configurada no departamento. A sugestão (modo assistido)
        // continua sendo o maior score, sem mover o cursor do round-robin.
        if (evaluation.Source == AiAssignmentDecisionSource.AiUnavailable)
            await ApplyConfiguredFallbackChoiceAsync(evaluation, ct);

        ticket.AssignedToUserId = evaluation.ChosenUserId;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await activityLog.LogActivityAsync(
            ticket.Id, TicketActivityType.AiAssigned, triggeredByUserId,
            oldValue: null,
            newValue: evaluation.ChosenUserId.Value.ToString(),
            comment: BuildActivityComment(evaluation));

        await notification.PublishAsync(new NotificationPublishRequest(
            "ticket.assigned", "tickets", "Ticket atribuído pela triagem por IA",
            $"Ticket #{ticket.Id}",
            NotificationSeverity.Informational,
            new { ticketId = ticket.Id, decisionId = evaluation.DecisionId, confidence = evaluation.Confidence },
            evaluation.ChosenUserId.Value), ct);

        return await PersistAsync(evaluation, apply: true, null, triggeredByUserId, ct);
    }

    // ── Ciclo periódico por cliente ──────────────────────────────────────

    public async Task<TriageCycleResult> ProcessDueAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var queueScopes = await queueRepository.ListPendingClientScopesAsync(MaxScopesPerRun, ct);
        var sweepScopes = await ListSweepScopesAsync(MaxScopesPerRun, ct);
        var scopes = queueScopes.Concat(sweepScopes).Distinct().ToList();

        if (scopes.Count == 0)
            return new TriageCycleResult(0, 0, 0, new Dictionary<Guid, int>(), 0);

        var states = await db.ProcessingScopeStates
            .Where(s => s.ScopeType == ProcessingScopeTypes.TicketTriage)
            .ToListAsync(ct);
        var stateByScope = states.ToDictionary(s => s.ScopeId);

        var triagedByClient = new Dictionary<Guid, int>();
        var scopesProcessed = 0;
        var triaged = 0;
        var swept = 0;

        foreach (var scopeId in scopes)
        {
            ct.ThrowIfCancellationRequested();

            var settings = (await configurationResolver
                .ResolveBackgroundProcessingAsync(NormalizeScope(scopeId), ct)).Triage;

            if (!settings.Enabled) continue;

            // Vencimento por cliente: o tick do job é a granularidade mínima.
            if (stateByScope.TryGetValue(scopeId, out var state)
                && DateTime.UtcNow - state.LastRunAt < TimeSpan.FromSeconds(settings.IntervalSeconds))
            {
                continue;
            }

            var quota = Math.Max(1, Math.Min(settings.BatchSize, settings.MaxPerClientPerRun));

            var scopeTriaged = await ProcessQueueForClientAsync(scopeId, quota, settings, ct);
            var scopeSwept = await SweepUnassignedForClientAsync(scopeId, quota, settings, ct);

            triaged += scopeTriaged;
            swept += scopeSwept;
            scopesProcessed++;
            triagedByClient[scopeId] = scopeTriaged;

            await SaveScopeStateAsync(stateByScope, scopeId, scopeTriaged, scopeSwept, ct);
        }

        stopwatch.Stop();
        return new TriageCycleResult(scopesProcessed, triaged, swept, triagedByClient, stopwatch.ElapsedMilliseconds);
    }

    private async Task<int> ProcessQueueForClientAsync(
        Guid scopeId, int quota, TicketTriageProcessingSettings settings, CancellationToken ct)
    {
        var items = await queueRepository.ClaimBatchForClientAsync(scopeId, quota, ct);
        var processed = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ticket = await db.Tickets.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == item.TicketId && t.DeletedAt == null, ct);

                if (ticket is null)
                {
                    await queueRepository.MarkSkippedAsync(item.Id, "ticket_not_found", ct);
                    continue;
                }

                if (ticket.AssignedToUserId.HasValue)
                {
                    await queueRepository.MarkSkippedAsync(item.Id, "already_assigned", ct);
                    continue;
                }

                var department = await db.Departments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == item.DepartmentId, ct);

                if (department is null || department.AssignmentStrategy != (int)TicketAssignmentStrategy.AiTriage)
                {
                    await queueRepository.MarkSkippedAsync(item.Id, "strategy_changed", ct);
                    continue;
                }

                if (department.AiAssignmentMode == (int)AiAssignmentMode.AutoAssign)
                    await ApplyAsync(item.TicketId, null, ct);
                else
                    await RecommendAsync(item.TicketId, null, ct);

                await queueRepository.MarkDoneAsync(item.Id, ct);
                processed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Triagem por IA falhou para o chamado {TicketId} (tentativa {Attempts}).",
                    item.TicketId, item.Attempts);

                if (item.Attempts >= Math.Max(1, settings.MaxAttempts))
                {
                    var fellBack = await TryFallbackSafeAsync(item.TicketId, "ai_failed", ct);
                    if (fellBack)
                        await queueRepository.MarkDoneAsync(item.Id, ct);
                    else
                        await queueRepository.MarkSkippedAsync(item.Id, "ai_failed_no_fallback", ct);
                }
                else
                {
                    await queueRepository.MarkFailedAsync(item.Id, ex.Message, Backoff(item.Attempts), ct);
                }
            }
        }

        return processed;
    }

    /// <summary>
    /// Escopos com chamados AiTriage sem responsável. Cobre o modo somente-lotes
    /// (EnqueueOnCreate = false) e a rede de segurança do modo com fila.
    /// </summary>
    private async Task<List<Guid>> ListSweepScopesAsync(int maxScopes, CancellationToken ct)
    {
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId == null && t.DepartmentId != null)
            .Join(db.Departments.AsNoTracking().Where(d =>
                    d.AssignmentStrategy == (int)TicketAssignmentStrategy.AiTriage
                    && d.AiAssignmentMode == (int)AiAssignmentMode.AutoAssign),
                t => t.DepartmentId!.Value, d => d.Id,
                (t, d) => new { ClientId = (Guid?)d.ClientId, t.CreatedAt })
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Clamp(maxScopes, 1, 500))
            .ToListAsync(ct);

        var scopes = new List<Guid>();
        foreach (var row in rows)
        {
            var scopeId = row.ClientId ?? Guid.Empty;
            if (!scopes.Contains(scopeId))
                scopes.Add(scopeId);
        }

        return scopes;
    }

    private async Task<int> SweepUnassignedForClientAsync(
        Guid scopeId, int quota, TicketTriageProcessingSettings settings, CancellationToken ct)
    {
        // No modo com fila a varredura é a rede de segurança (RetryAfterMinutes); no
        // modo somente-lotes ela é o caminho principal e usa BatchDelaySeconds.
        var minAgeMinutes = settings.EnqueueOnCreate
            ? settings.RetryAfterMinutes
            : Math.Max(0, settings.BatchDelaySeconds) / 60.0;

        var cutoff = DateTime.UtcNow.AddMinutes(-minAgeMinutes);
        var limit = Math.Clamp(quota, 1, 200);

        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId == null
                        && t.DepartmentId != null && t.CreatedAt <= cutoff);

        List<Guid> stale;
        if (scopeId == Guid.Empty)
        {
            stale = await baseQuery
                .Where(t => db.Departments.Any(d => d.Id == t.DepartmentId!.Value
                    && d.AssignmentStrategy == (int)TicketAssignmentStrategy.AiTriage
                    && d.AiAssignmentMode == (int)AiAssignmentMode.AutoAssign
                    && d.ClientId == null))
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .Take(limit)
                .ToListAsync(ct);
        }
        else
        {
            var scope = scopeId;
            stale = await baseQuery
                .Where(t => db.Departments.Any(d => d.Id == t.DepartmentId!.Value
                    && d.AssignmentStrategy == (int)TicketAssignmentStrategy.AiTriage
                    && d.AiAssignmentMode == (int)AiAssignmentMode.AutoAssign
                    && d.ClientId == scope))
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .Take(limit)
                .ToListAsync(ct);
        }

        var assigned = 0;
        foreach (var ticketId in stale)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryFallbackSafeAsync(ticketId, "sweep_timeout", ct))
                assigned++;
        }

        return assigned;
    }

    private async Task SaveScopeStateAsync(
        Dictionary<Guid, ProcessingScopeState> stateByScope,
        Guid scopeId, int triagedCount, int sweptCount, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            triaged = triagedCount,
            swept = sweptCount,
            type = ProcessingScopeTypes.TicketTriage
        });

        if (stateByScope.TryGetValue(scopeId, out var state))
        {
            state.LastRunAt = now;
            state.LastResultJson = payload;
            state.UpdatedAt = now;
        }
        else
        {
            state = new ProcessingScopeState
            {
                Id = Guid.NewGuid(),
                ScopeType = ProcessingScopeTypes.TicketTriage,
                ScopeId = scopeId,
                LastRunAt = now,
                LastResultJson = payload,
                UpdatedAt = now
            };
            db.ProcessingScopeStates.Add(state);
            stateByScope[scopeId] = state;
        }

        await db.SaveChangesAsync(ct);
    }

    private static Guid? NormalizeScope(Guid scopeId) => scopeId == Guid.Empty ? null : scopeId;

    // ── Avaliação ────────────────────────────────────────────────────────

    private async Task<TriageEvaluation?> EvaluateAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.DeletedAt == null, ct);
        if (ticket?.DepartmentId is null) return null;

        var department = await db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == ticket.DepartmentId!.Value, ct);
        if (department is null) return null;

        // Equipe candidata via resolvedor único (ponto de extensão da herança de
        // departamento — hoje apenas os membros do próprio departamento).
        var members = await teamResolver.ResolveMembersAsync(department.Id, ct);

        var evaluation = new TriageEvaluation
        {
            Ticket = ticket,
            Department = department,
            DecisionId = Guid.NewGuid(),
            MemberNames = members.ToDictionary(m => m.UserId, m => m.DisplayName)
        };

        var eligible = members.Where(m => m.AcceptsAiAssignment).ToList();

        if (eligible.Count == 0)
        {
            evaluation.ChosenUserId = null;
            evaluation.Source = AiAssignmentDecisionSource.NoCandidates;
            evaluation.Rationale = members.Count == 0
                ? "Departamento sem membros ativos."
                : "Todos os membros estão com a triagem por IA desativada.";
            return evaluation;
        }

        var userIds = eligible.Select(m => m.UserId).ToList();

        // Escopo do cliente do departamento: define janela e política de snapshot.
        var metrics = (await metricsService.GetMetricsForUsersAsync(userIds, department.ClientId, ct))
            .ToDictionary(m => m.UserId);

        // Auditoria: idade do snapshot mais antigo usado nesta decisão (null = sem
        // snapshot; o ciclo agendado preenche).
        var snapshotAges = metrics.Values
            .Where(m => m.ComputedAt.HasValue)
            .Select(m => (int)Math.Max(0, (DateTime.UtcNow - m.ComputedAt!.Value).TotalMinutes))
            .ToList();
        evaluation.MetricsSnapshotAgeMinutes = snapshotAges.Count > 0 ? snapshotAges.Max() : null;

        var affinityHits = department.AiAssignmentUseAffinity
            ? await affinityRepository.FindSimilarResolvedAsync(
                BuildAffinityQuery(ticket), ticket.ClientId, AffinityPoolSize, ct)
            : [];

        var affinityByUser = affinityHits
            .GroupBy(h => h.UserId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.OrderByDescending(h => h.Similarity).First();
                    return (Similarity: Math.Clamp(best.Similarity, 0, 1), best.TicketTitle);
                });

        var difficulty = await difficultyAssessor.AssessAsync(ticket, affinityHits, ct);
        var tags = TicketSignalExtractor.ExtractTags(ticket);
        var departmentMaxOpen = metrics.Values.Select(m => m.OpenNow).DefaultIfEmpty(0).Max();
        var medianResolution = Median(metrics.Values
            .Where(m => m.AvgResolutionMinutes.HasValue)
            .Select(m => m.AvgResolutionMinutes!.Value)
            .ToList());

        var context = new TicketScoringContext(difficulty.Level, tags, ticket.Category, medianResolution);
        var weights = ParseWeights(department.AiAssignmentWeightsJson);

        var candidates = eligible
            .Select(member =>
            {
                var memberMetrics = metrics.TryGetValue(member.UserId, out var found)
                    ? found
                    : new TechnicianMetricsDto(member.UserId, TechnicianMetricsService.DefaultWindowDays,
                        0, 0, 0, null, null, null, 0, 0, null, 0, null, [], [], DateTime.UtcNow);

                var affinity = affinityByUser.TryGetValue(member.UserId, out var hit)
                    ? hit
                    : (Similarity: 0d, TicketTitle: (string?)null);

                return AssignmentScorer.Score(
                    new TechnicianCandidateInput(
                        member.UserId, member.DisplayName, ParseSkillTags(member.SkillTagsJson),
                        member.SkillLevel, member.MaxOpenTickets, member.Weight, member.AcceptsAiAssignment,
                        memberMetrics, affinity.Similarity, affinity.TicketTitle),
                    context,
                    weights,
                    departmentMaxOpen);
            })
            .ToList();

        var pool = AssignmentScorer
            .Rank(candidates)
            .Take(Math.Clamp(department.AiAssignmentMaxCandidates, 1, 20))
            .ToList();

        evaluation.Candidates = pool;
        evaluation.Difficulty = difficulty.Level;
        evaluation.Tags = tags;

        var top = pool[0];

        // Orçamento de tokens derivado da capacidade real do modelo + teto do
        // departamento (nunca ultrapassa o modelo nem o teto do produto).
        var siteId = await ResolvePromptSiteIdAsync(ticket, ct);
        AiTokenBudgetDto? budget = null;
        if (siteId.HasValue)
        {
            budget = await tokenBudgetResolver.ResolveForSiteAsync(
                siteId.Value, AiTokenLimits.DefaultDepartmentOutputTokens,
                department.AiAssignmentMaxOutputTokens, ct);
            evaluation.MaxOutputTokens = budget.MaxOutputTokens;
        }

        TriageResponse? response = null;
        try
        {
            if (siteId is null)
                throw new InvalidOperationException("Chamado sem site para resolver a configuração de IA.");

            var call = await AskModelAsync(ticket, department, context, pool, siteId.Value, budget!, ct);
            evaluation.PromptChars = call.PromptChars;
            response = call.Response;
            if (response is not null)
            {
                evaluation.Model = response.Model;
                evaluation.TokensUsed = response.TokensUsed;
            }
        }
        catch (AiUsageLimitException ex)
        {
            // Rate limit/budget: não é falha do provedor — degrada para o
            // fallback determinístico com motivo auditável.
            logger.LogInformation(
                "Triagem por IA bloqueada por limite de uso ({Reason}); usando fallback determinístico.", ex.Reason);
            evaluation.Source = AiAssignmentDecisionSource.AiBudgetExceeded;
            evaluation.Rationale = "Limite de uso de IA atingido: " + ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation(
                "Triagem por IA indisponível ({Message}); usando score determinístico.", ex.Message);
            evaluation.Source = AiAssignmentDecisionSource.AiUnavailable;
            evaluation.Rationale = "IA indisponível: " + ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao consultar a IA na triagem do chamado {TicketId}.", ticket.Id);
            evaluation.Source = AiAssignmentDecisionSource.AiUnavailable;
            evaluation.Rationale = "Falha ao consultar a IA.";
        }

        var chosen = top;

        if (response is not null)
        {
            var fromModel = pool.FirstOrDefault(c => c.UserId == response.ChosenUserId);
            var confidence = Math.Clamp(response.Confidence, 0, 1);

            if (fromModel is null)
            {
                evaluation.Source = AiAssignmentDecisionSource.FallbackScore;
                evaluation.Rationale = "A IA escolheu um usuário fora dos candidatos; usado o maior score.";
            }
            else if (confidence < department.AiAssignmentMinConfidence)
            {
                evaluation.Source = AiAssignmentDecisionSource.FallbackScore;
                evaluation.Confidence = confidence;
                evaluation.RationaleFromModel = response.Rationale;
                evaluation.Rationale =
                    $"Confiança da IA ({confidence:0.00}) abaixo do mínimo ({department.AiAssignmentMinConfidence:0.00}); usado o maior score.";
                chosen = fromModel.Score >= top.Score ? fromModel : top;

                evaluation.ChosenUserId = chosen.UserId;
                evaluation.Score = chosen.Score;
                evaluation.Difficulty = response.Difficulty is >= 1 and <= 5
                    ? response.Difficulty!.Value
                    : evaluation.Difficulty;
                return evaluation;
            }
            else
            {
                chosen = fromModel;
                evaluation.Source = AiAssignmentDecisionSource.Ai;
                evaluation.Confidence = confidence;
                evaluation.RationaleFromModel = response.Rationale;
                if (response.Difficulty is >= 1 and <= 5)
                    evaluation.Difficulty = response.Difficulty.Value;

                // Capacidade é restrição dura: se a IA escolheu alguém no teto e
                // existe alternativa livre com score equivalente, usa a alternativa.
                if (fromModel.OverCapacity)
                {
                    var free = pool.FirstOrDefault(c => !c.OverCapacity
                                                        && c.UserId != fromModel.UserId
                                                        && c.Score >= fromModel.Score);
                    if (free is not null)
                    {
                        chosen = free;
                        evaluation.Source = AiAssignmentDecisionSource.FallbackScore;
                        evaluation.Rationale =
                            "A escolha da IA estava no teto de chamados; usada alternativa livre com score equivalente.";
                    }
                }
            }
        }
        else if (evaluation.Source is null)
        {
            evaluation.Source = AiAssignmentDecisionSource.FallbackScore;
            evaluation.Rationale = "Sem resposta válida da IA; usado o maior score determinístico.";
            evaluation.Confidence = 0;
        }
        else
        {
            evaluation.Confidence = 0;
        }

        evaluation.ChosenUserId = chosen.UserId;
        evaluation.Score = chosen.Score;
        evaluation.Rationale ??= $"Maior score determinístico ({chosen.Score:0.00}).";
        return evaluation;
    }

    private async Task<ModelCallResult> AskModelAsync(
        Ticket ticket, Department department, TicketScoringContext context,
        IReadOnlyList<AssignmentCandidateDto> candidates, Guid siteId,
        AiTokenBudgetDto budget, CancellationToken ct)
    {
        var systemPrompt =
            "Você é o despachante de um service desk de TI. Escolha o atendente mais adequado para o chamado " +
            "considerando competências, afinidade com problemas semelhantes já resolvidos, desempenho histórico, " +
            "carga atual, satisfação do usuário (CSAT) e qualidade de SLA. " +
            "Escolha obrigatoriamente entre os candidatos informados e nunca invente um identificador. " +
            "Responda APENAS com JSON válido, sem markdown, no formato: " +
            "{\"chosenUserId\":\"<uuid de um candidato>\",\"confidence\":<0..1>,\"difficulty\":<1..5>," +
            "\"tags\":[\"<tag>\"],\"rationale\":\"<1-3 frases explicando a escolha>\",\"alternateUserId\":null ou \"<uuid>\"}";

        // O truncamento segue o orçamento de prompt derivado da janela de
        // contexto do modelo (antes era um teto fixo de 4000 caracteres).
        var description = ticket.Description ?? string.Empty;
        if (description.Length > budget.MaxPromptChars)
            description = description[..budget.MaxPromptChars] + "...";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CHAMADO");
        sb.AppendLine($"Título: {ticket.Title}");
        sb.AppendLine($"Descrição: {description}");
        sb.AppendLine($"Categoria: {ticket.Category ?? "N/A"}");
        sb.AppendLine($"Prioridade: {ticket.Priority}");
        sb.AppendLine($"Dificuldade estimada (heurística): {context.Difficulty}");
        if (context.Tags.Count > 0)
            sb.AppendLine($"Tags: {string.Join(", ", context.Tags)}");
        sb.AppendLine();
        sb.AppendLine("CANDIDATOS (somente estes podem ser escolhidos)");

        foreach (var candidate in candidates)
        {
            sb.Append("- userId=").Append(candidate.UserId)
              .Append(" nome=").Append(candidate.UserName ?? "?")
              .Append(" score=").Append(candidate.Score.ToString("0.00"))
              .Append(" abertos=").Append(candidate.OpenNow)
              .Append(" skill=").Append(candidate.SkillScore.ToString("0.00"))
              .Append(" afinidade=").Append(candidate.AffinityScore.ToString("0.00"))
              .Append(" performance=").Append(candidate.PerformanceScore.ToString("0.00"))
              .Append(" csat=").Append(candidate.CsatScore.ToString("0.00"))
              .Append(" sla=").Append(candidate.SlaScore.ToString("0.00"))
              .AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(department.AiAssignmentInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("ORIENTAÇÕES DO GESTOR");
            sb.AppendLine(department.AiAssignmentInstructions);
        }

        var prompt = sb.ToString();
        var response = await aiChat.ProcessTicketPromptJsonAsync(
            systemPrompt, prompt, siteId,
            budget.MaxOutputTokens, Temperature, "json_object", department.Id, ct);

        var parsed = ParseResponse(response.Content);
        return new ModelCallResult(
            parsed is null
                ? null
                : parsed with { Model = response.ModelVersion, TokensUsed = response.TokensUsed },
            prompt.Length);
    }

    private static TriageResponse? ParseResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var trimmed = content.Trim();
        if (trimmed.StartsWith(Fence, StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf(Fence, StringComparison.Ordinal);
            if (firstLineEnd > 0 && lastFence > firstLineEnd)
                trimmed = trimmed[(firstLineEnd + 1)..lastFence].Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<TriageResponse>(trimmed, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Persistência ─────────────────────────────────────────────────────

    private async Task<TicketAssignmentResultDto> PersistAsync(
        TriageEvaluation evaluation, bool apply, string? notAppliedReason,
        Guid? triggeredByUserId, CancellationToken ct)
    {
        var decision = new TicketAssignmentDecision
        {
            Id = evaluation.DecisionId,
            TicketId = evaluation.Ticket.Id,
            DepartmentId = evaluation.Department.Id,
            Mode = evaluation.Department.AiAssignmentMode,
            StrategySource = evaluation.Source ?? AiAssignmentDecisionSource.FallbackScore,
            Difficulty = evaluation.Difficulty,
            ChosenUserId = evaluation.ChosenUserId,
            Confidence = evaluation.Confidence,
            Score = evaluation.Score,
            CandidatesJson = JsonSerializer.Serialize(evaluation.Candidates),
            Rationale = BuildRationale(evaluation),
            Model = evaluation.Model,
            TokensUsed = evaluation.TokensUsed,
            MaxOutputTokens = evaluation.MaxOutputTokens,
            PromptChars = evaluation.PromptChars,
            MetricsSnapshotAgeMinutes = evaluation.MetricsSnapshotAgeMinutes,
            Applied = apply,
            NotAppliedReason = apply ? null : notAppliedReason,
            OverriddenByUserId = triggeredByUserId,
            CreatedAt = DateTime.UtcNow
        };

        db.TicketAssignmentDecisions.Add(decision);
        await db.SaveChangesAsync(ct);

        if (!apply && evaluation.ChosenUserId.HasValue && notAppliedReason == "suggest_only")
        {
            await activityLog.LogActivityAsync(
                evaluation.Ticket.Id, TicketActivityType.AiAssignmentSuggested, triggeredByUserId,
                oldValue: null,
                newValue: evaluation.ChosenUserId.Value.ToString(),
                comment: BuildActivityComment(evaluation));

            await notification.PublishAsync(new NotificationPublishRequest(
                "ticket.assignment.suggested", "tickets", "Sugestão de responsável pela IA",
                $"Ticket #{evaluation.Ticket.Id}",
                NotificationSeverity.Informational,
                new { ticketId = evaluation.Ticket.Id, decisionId = decision.Id, confidence = evaluation.Confidence },
                evaluation.ChosenUserId.Value), ct);
        }

        return new TicketAssignmentResultDto(
            ToDto(decision, evaluation.Candidates),
            apply ? evaluation.ChosenUserId : null,
            apply,
            decision.StrategySource);
    }

    private static TicketAssignmentDecisionDto ToDto(
        TicketAssignmentDecision decision, IReadOnlyList<AssignmentCandidateDto> candidates)
        => new(
            decision.Id, decision.TicketId, decision.DepartmentId, decision.Mode, decision.StrategySource,
            decision.Difficulty, decision.ChosenUserId,
            candidates.FirstOrDefault(c => c.UserId == decision.ChosenUserId)?.UserName,
            decision.Confidence, decision.Score, decision.Rationale, decision.Model, decision.TokensUsed,
            decision.Applied, decision.NotAppliedReason, decision.OverriddenAt, decision.OverriddenByUserId,
            decision.CreatedAt, candidates, decision.MaxOutputTokens, decision.PromptChars,
            decision.MetricsSnapshotAgeMinutes);

    private static TicketAssignmentResultDto NotFoundResult(Guid ticketId)
        => new(
            new TicketAssignmentDecisionDto(
                Guid.Empty, ticketId, Guid.Empty, 0, AiAssignmentDecisionSource.Error, 3, null, null,
                0, 0, "Chamado não encontrado.", null, 0, false, null, null, null, DateTime.UtcNow, [], 0, 0, null),
            null, false, AiAssignmentDecisionSource.Error);

    /// <summary>
    /// Substitui a escolha por score pela estratégia determinística configurada
    /// no departamento quando a IA está indisponível (aplicação automática).
    /// </summary>
    private async Task ApplyConfiguredFallbackChoiceAsync(TriageEvaluation evaluation, CancellationToken ct)
    {
        var fallbackUserId = await assignmentService.ResolveFallbackAsync(
            evaluation.Department.Id, evaluation.Department.AiAssignmentFallbackStrategy, ct);

        if (!fallbackUserId.HasValue || fallbackUserId.Value == evaluation.ChosenUserId) return;

        var existing = evaluation.Candidates.FirstOrDefault(c => c.UserId == fallbackUserId.Value);
        evaluation.ChosenUserId = fallbackUserId;

        if (existing is not null)
        {
            evaluation.Score = existing.Score;
        }
        else
        {
            var name = evaluation.MemberNames.TryGetValue(fallbackUserId.Value, out var found) ? found : null;
            evaluation.Candidates = evaluation.Candidates
                .Append(new AssignmentCandidateDto(
                    fallbackUserId.Value, name, 0, 0, 0, 0, 0, 0, 0, false, 0, null))
                .ToList();
            evaluation.Score = 0;
        }

        evaluation.RationaleFromModel = null;
        evaluation.Rationale =
            "IA indisponível: aplicada a estratégia de fallback configurada no departamento.";
    }

    // ── Fallback determinístico ──────────────────────────────────────────

    private async Task<bool> TryFallbackSafeAsync(Guid ticketId, string reason, CancellationToken ct)
    {
        try
        {
            return await ApplyFallbackAsync(ticketId, reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallback da triagem por IA falhou para o chamado {TicketId}.", ticketId);
            return false;
        }
    }

    private async Task<bool> ApplyFallbackAsync(Guid ticketId, string reason, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.DeletedAt == null, ct);
        if (ticket?.DepartmentId is null || ticket.AssignedToUserId.HasValue) return false;

        var department = await db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == ticket.DepartmentId!.Value, ct);
        if (department is null) return false;

        var assignee = await assignmentService.ResolveFallbackAsync(
            department.Id, department.AiAssignmentFallbackStrategy, ct);
        if (assignee is null) return false;

        ticket.AssignedToUserId = assignee;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        db.TicketAssignmentDecisions.Add(new TicketAssignmentDecision
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            DepartmentId = department.Id,
            Mode = department.AiAssignmentMode,
            StrategySource = AiAssignmentDecisionSource.FallbackStrategy,
            Difficulty = 3,
            ChosenUserId = assignee,
            Confidence = 0,
            Score = 0,
            Rationale = $"Fallback determinístico da triagem por IA ({reason}).",
            Applied = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        await activityLog.LogActivityAsync(
            ticket.Id, TicketActivityType.AiAssigned, null, null, assignee.Value.ToString(),
            $"Fallback determinístico da triagem por IA ({reason}).");

        await notification.PublishAsync(new NotificationPublishRequest(
            "ticket.assigned", "tickets", "Ticket atribuído (fallback da triagem por IA)",
            $"Ticket #{ticket.Id}", NotificationSeverity.Informational,
            new { ticketId = ticket.Id }, assignee.Value), ct);

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TimeSpan Backoff(int attempts)
        => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempts, 1, 8)) * 5));

    /// <summary>
    /// Chamados criados pelo agent ou por alertas podem não ter site. A resolução
    /// de configuração de IA exige um site existente para aplicar a herança
    /// Site -> Cliente -> Servidor, então usamos um site do mesmo cliente.
    /// </summary>
    private Task<Guid?> ResolvePromptSiteIdAsync(Ticket ticket, CancellationToken ct)
        => AiSiteScopeResolver.ResolveAsync(db, ticket.SiteId, ticket.ClientId, ct);

    private static string BuildAffinityQuery(Ticket ticket)
        => $"{ticket.Title} {ticket.Category}".Trim();

    private static string BuildRationale(TriageEvaluation evaluation)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(evaluation.RationaleFromModel))
            parts.Add(evaluation.RationaleFromModel!);
        if (!string.IsNullOrWhiteSpace(evaluation.Rationale))
            parts.Add(evaluation.Rationale!);

        var chosen = evaluation.Candidates.FirstOrDefault(c => c.UserId == evaluation.ChosenUserId);
        if (chosen is not null && chosen.Score > 0)
            parts.Add($"score {chosen.Score:0.00} (skill {chosen.SkillScore:0.00}, afinidade {chosen.AffinityScore:0.00}, {chosen.OpenNow} abertos)");

        return string.Join(" | ", parts);
    }

    private static string BuildActivityComment(TriageEvaluation evaluation)
    {
        var confidence = evaluation.Confidence > 0 ? $" confiança {evaluation.Confidence:0.00}" : string.Empty;
        return $"Dificuldade {evaluation.Difficulty}; origem {evaluation.Source}{confidence}.";
    }

    private static IReadOnlyList<string> ParseSkillTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AiAssignmentWeightsDto ParseWeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return AssignmentScorer.DefaultWeights;
        try
        {
            return JsonSerializer.Deserialize<AiAssignmentWeightsDto>(json, JsonOptions)
                   ?? AssignmentScorer.DefaultWeights;
        }
        catch (JsonException)
        {
            return AssignmentScorer.DefaultWeights;
        }
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(v => v).ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private sealed class TriageEvaluation
    {
        public required Ticket Ticket { get; init; }
        public required Department Department { get; init; }
        public required Guid DecisionId { get; init; }
        public IReadOnlyDictionary<Guid, string?> MemberNames { get; init; } = new Dictionary<Guid, string?>();
        public IReadOnlyList<AssignmentCandidateDto> Candidates { get; set; } = [];
        public Guid? ChosenUserId { get; set; }
        public double Confidence { get; set; }
        public double Score { get; set; }
        public int Difficulty { get; set; } = 3;
        public IReadOnlyList<string> Tags { get; set; } = [];
        public string? Source { get; set; }
        public string? Rationale { get; set; }
        public string? RationaleFromModel { get; set; }
        public string? Model { get; set; }
        public int TokensUsed { get; set; }
        public int MaxOutputTokens { get; set; }
        public int PromptChars { get; set; }
        public int? MetricsSnapshotAgeMinutes { get; set; }
    }

    /// <summary>Resposta do modelo + tamanho do prompt enviado (auditoria do orçamento).</summary>
    private sealed record ModelCallResult(TriageResponse? Response, int PromptChars);

    private sealed record TriageResponse(
        Guid ChosenUserId,
        double Confidence,
        int? Difficulty = null,
        List<string>? Tags = null,
        string? Rationale = null,
        Guid? AlternateUserId = null,
        string? Model = null,
        int TokensUsed = 0);
}
