using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AgentAutoLabelingService : IAgentAutoLabelingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private const string EnabledRulesCacheKey = AgentLabelingCacheKeys.EnabledRules;
    private const int EnabledRulesCacheTtlSeconds = AgentLabelingCacheKeys.EnabledRulesTtlSeconds;

    private readonly DiscoveryDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly IAgentHardwareRepository _hardwareRepository;
    private readonly IAgentSoftwareRepository _softwareRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IRedisService _redisService;
    private readonly ILogger<AgentAutoLabelingService> _logger;

    private readonly record struct PreparedRule(
        Guid RuleId,
        string Label,
        AgentLabelApplyMode ApplyMode,
        AgentLabelRuleExpressionNodeDto Expression);

    private readonly record struct CustomFieldEntry(string ValueJson, CustomFieldDataType DataType);

    /// <summary>Mudanca de label detectada durante a avaliacao (usada para auditoria).</summary>
    private readonly record struct AgentLabelChange(
        Guid AgentId,
        string Label,
        AgentLabelSourceType SourceType,
        string Action);

    public AgentAutoLabelingService(
        DiscoveryDbContext db,
        IAgentRepository agentRepository,
        IAgentHardwareRepository hardwareRepository,
        IAgentSoftwareRepository softwareRepository,
        IAgentLabelRuleRepository ruleRepository,
        ISiteRepository siteRepository,
        IRedisService redisService,
        ILogger<AgentAutoLabelingService> logger)
    {
        _db = db;
        _agentRepository = agentRepository;
        _hardwareRepository = hardwareRepository;
        _softwareRepository = softwareRepository;
        _ruleRepository = ruleRepository;
        _siteRepository = siteRepository;
        _redisService = redisService;
        _logger = logger;
    }

    public async Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        var rules = await PrepareEnabledRulesAsync(cancellationToken);
        if (rules.Count == 0)
            return;

        await EvaluateAgentWithRulesAsync(agentId, reason, rules, cancellationToken);
    }

    public async Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rules = await GetCachedEnabledRulesAsync();
        return rules.Count > 0;
    }

    public async Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default)
    {
        await ReprocessAllAgentsAsync(reason, batchSize, progress: null, cancellationToken);
    }

    public async Task ReprocessAllAgentsAsync(
        string reason,
        int batchSize,
        IProgress<AgentLabelReprocessProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var safeBatchSize = Math.Clamp(batchSize, 25, 1000);
        var rules = await PrepareEnabledRulesAsync(cancellationToken);

        var totalAgents = await _db.Agents.AsNoTracking().CountAsync(cancellationToken);
        if (rules.Count == 0)
        {
            progress?.Report(new AgentLabelReprocessProgress(0, totalAgents, true, "Nenhuma regra habilitada."));
            return;
        }

        var processed = 0;
        Guid? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Traz as entidades do lote em uma unica query (antes: 1 GetByIdAsync por agente).
            var currentBatch = await _db.Agents
                .AsNoTracking()
                .Where(agent => !cursor.HasValue || agent.Id.CompareTo(cursor.Value) > 0)
                .OrderBy(agent => agent.Id)
                .Take(safeBatchSize)
                .ToListAsync(cancellationToken);

            if (currentBatch.Count == 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            await EvaluateAgentsBatchAsync(currentBatch, reason, rules, cancellationToken);

            processed += currentBatch.Count;
            cursor = currentBatch[^1].Id;
            progress?.Report(new AgentLabelReprocessProgress(processed, totalAgents, false, null));
        }

        progress?.Report(new AgentLabelReprocessProgress(processed, totalAgents, true, null));
    }

    public async Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(request.AgentId);
        if (agent is null)
            throw new InvalidOperationException("Agent not found.");

        // Carrega hardware/software apenas quando a expressao realmente os usa, como no
        // caminho em lote. Antes o dry-run pagava o custo do inventario de software
        // completo mesmo para uma regra que so olha o hostname.
        var hardware = HasHardwareConditions(request.Expression)
            ? await _hardwareRepository.GetByAgentIdAsync(request.AgentId)
            : null;
        var software = HasSoftwareConditions(request.Expression)
            ? (await _softwareRepository.GetCurrentByAgentIdAsync(request.AgentId)).ToList()
            : [];

        var customFieldValues = HasCustomFieldConditions(request.Expression)
            ? await LoadCustomFieldValuesForAgentAsync(request.AgentId, agent.SiteId, cancellationToken)
            : null;

        var disks = HasDiskConditions(request.Expression)
            ? (await _hardwareRepository.GetComponentsAsync(request.AgentId)).Disks
            : null;

        var matched = EvaluateNode(request.Expression, agent, hardware, software, customFieldValues, disks);

        var automaticLabels = await _db.AgentLabels
            .AsNoTracking()
            .Where(label => label.AgentId == request.AgentId && label.SourceType == AgentLabelSourceType.Automatic)
            .Select(label => label.Label)
            .OrderBy(label => label)
            .ToListAsync(cancellationToken);

        var hasLabel = !string.IsNullOrWhiteSpace(request.Label)
            && automaticLabels.Contains(request.Label, StringComparer.OrdinalIgnoreCase);

        return new AgentLabelRuleDryRunResponse
        {
            AgentId = request.AgentId,
            Matched = matched,
            Label = request.Label,
            WouldAddLabel = matched && !string.IsNullOrWhiteSpace(request.Label) && !hasLabel,
            WouldRemoveLabel =
                !matched
                && request.ApplyMode == AgentLabelApplyMode.ApplyAndRemove
                && !string.IsNullOrWhiteSpace(request.Label)
                && hasLabel,
            CurrentAutomaticLabels = automaticLabels
        };
    }

    /// <summary>
    /// Avalia uma expressao contra uma amostra da frota e extrapola o impacto.
    /// Permite responder "quantos agentes esta regra afetaria?" antes de salvar,
    /// em vez de exigir a escolha de cliente+site e limitar a 25-100 agentes.
    /// </summary>
    public async Task<AgentLabelRuleImpactResponse> EvaluateImpactAsync(
        AgentLabelRuleImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sampleSize = Math.Clamp(request.SampleSize, 10, 500);

        var query = _db.Agents.AsNoTracking();
        if (request.SiteId.HasValue)
        {
            query = query.Where(agent => agent.SiteId == request.SiteId.Value);
        }
        else if (request.ClientId.HasValue)
        {
            var siteIds = await _db.Sites
                .AsNoTracking()
                .Where(site => site.ClientId == request.ClientId.Value)
                .Select(site => site.Id)
                .ToListAsync(cancellationToken);
            query = query.Where(agent => siteIds.Contains(agent.SiteId));
        }

        var totalAgents = await query.CountAsync(cancellationToken);

        // Amostra deterministica por Id: estavel entre execucoes e barata (sem TABLESAMPLE).
        var agents = await query
            .OrderBy(agent => agent.Id)
            .Take(sampleSize)
            .ToListAsync(cancellationToken);

        if (agents.Count == 0)
        {
            return new AgentLabelRuleImpactResponse { EstimatedTotalAgents = totalAgents };
        }

        var agentIds = agents.Select(agent => agent.Id).ToList();
        var needsCustomFields = HasCustomFieldConditions(request.Expression);
        var needsDisks = HasDiskConditions(request.Expression);
        var needsHardware = HasHardwareConditions(request.Expression);
        var needsSoftware = HasSoftwareConditions(request.Expression);

        var hardwareByAgent = needsHardware
            ? await _hardwareRepository.GetByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, AgentHardwareInfo>();
        var softwareByAgent = needsSoftware
            ? await _softwareRepository.GetCurrentByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, IReadOnlyList<AgentInstalledSoftware>>();
        var disksByAgent = needsDisks
            ? await _hardwareRepository.GetDisksByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, IReadOnlyList<DiskInfo>>();
        var customFieldsByAgent = needsCustomFields
            ? await LoadCustomFieldValuesForAgentsAsync(agents, cancellationToken)
            : new Dictionary<Guid, IReadOnlyDictionary<Guid, CustomFieldEntry>>();

        var automaticLabelsByAgent = (await _db.AgentLabels
                .AsNoTracking()
                .Where(label => agentIds.Contains(label.AgentId) && label.SourceType == AgentLabelSourceType.Automatic)
                .Select(label => new { label.AgentId, label.Label })
                .ToListAsync(cancellationToken))
            .GroupBy(item => item.AgentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.Label).ToList());

        var normalizedLabel = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        var samples = new List<AgentLabelRuleImpactSample>(agents.Count);
        var matched = 0;
        var wouldAdd = 0;
        var wouldRemove = 0;

        foreach (var agent in agents)
        {
            hardwareByAgent.TryGetValue(agent.Id, out var hardware);
            softwareByAgent.TryGetValue(agent.Id, out var software);
            disksByAgent.TryGetValue(agent.Id, out var disks);
            customFieldsByAgent.TryGetValue(agent.Id, out var customFieldValues);

            var isMatch = EvaluateNode(
                request.Expression, agent, hardware, software ?? [], customFieldValues, disks);

            var currentLabels = automaticLabelsByAgent.TryGetValue(agent.Id, out var labels) ? labels : [];
            var hasLabel = normalizedLabel is not null
                && currentLabels.Contains(normalizedLabel, StringComparer.OrdinalIgnoreCase);

            var add = isMatch && normalizedLabel is not null && !hasLabel;
            var remove = !isMatch
                && request.ApplyMode == AgentLabelApplyMode.ApplyAndRemove
                && normalizedLabel is not null
                && hasLabel;

            if (isMatch) matched++;
            if (add) wouldAdd++;
            if (remove) wouldRemove++;

            samples.Add(new AgentLabelRuleImpactSample
            {
                AgentId = agent.Id,
                Hostname = agent.Hostname,
                DisplayName = agent.DisplayName,
                Matched = isMatch,
                WouldAddLabel = add,
                WouldRemoveLabel = remove,
                CurrentAutomaticLabels = currentLabels
            });
        }

        // Extrapola a taxa da amostra para a frota (estimativa, nao contagem exata).
        var ratio = agents.Count == 0 ? 0d : (double)matched / agents.Count;

        return new AgentLabelRuleImpactResponse
        {
            Sampled = agents.Count,
            Matched = matched,
            WouldAddLabel = wouldAdd,
            WouldRemoveLabel = wouldRemove,
            EstimatedTotalAgents = totalAgents,
            EstimatedMatched = (int)Math.Round(ratio * totalAgents),
            Truncated = totalAgents > agents.Count,
            Samples = samples
        };
    }

    /// <summary>Avalia um unico agente (caminho de evento isolado — custom fields, etc.).</summary>
    private async Task EvaluateAgentWithRulesAsync(
        Guid agentId,
        string reason,
        IReadOnlyList<PreparedRule> rules,
        CancellationToken cancellationToken)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return;

        await EvaluateAgentsBatchAsync([agent], reason, rules, cancellationToken);
    }

    /// <summary>
    /// Avalia um lote de agentes carregando todos os dados necessarios em um numero
    /// constante de queries (antes eram ~7 queries + ate 2 SaveChanges POR AGENTE).
    /// Regras cujas expressoes nao usam hardware/software/discos nao pagam o custo
    /// desses carregamentos.
    /// </summary>
    private async Task EvaluateAgentsBatchAsync(
        IReadOnlyList<Agent> agents,
        string reason,
        IReadOnlyList<PreparedRule> rules,
        CancellationToken cancellationToken)
    {
        if (agents.Count == 0 || rules.Count == 0)
            return;

        var agentIds = agents.Select(agent => agent.Id).ToList();
        var ruleIds = rules.Select(rule => rule.RuleId).ToList();

        var needsCustomFields = rules.Any(rule => HasCustomFieldConditions(rule.Expression));
        var needsDisks = rules.Any(rule => HasDiskConditions(rule.Expression));
        var needsHardware = rules.Any(rule => HasHardwareConditions(rule.Expression));
        var needsSoftware = rules.Any(rule => HasSoftwareConditions(rule.Expression));

        // Carregamentos em lote — 1 query cada, somente quando alguma regra precisa.
        var hardwareByAgent = needsHardware
            ? await _hardwareRepository.GetByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, AgentHardwareInfo>();
        var softwareByAgent = needsSoftware
            ? await _softwareRepository.GetCurrentByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, IReadOnlyList<AgentInstalledSoftware>>();
        var disksByAgent = needsDisks
            ? await _hardwareRepository.GetDisksByAgentIdsAsync(agentIds, cancellationToken)
            : new Dictionary<Guid, IReadOnlyList<DiskInfo>>();
        var customFieldsByAgent = needsCustomFields
            ? await LoadCustomFieldValuesForAgentsAsync(agents, cancellationToken)
            : new Dictionary<Guid, IReadOnlyDictionary<Guid, CustomFieldEntry>>();

        // 1 query para todos os matches do lote.
        var existingMatchesByAgent = (await _db.AgentLabelRuleMatches
                .Where(match => agentIds.Contains(match.AgentId) && ruleIds.Contains(match.RuleId))
                .ToListAsync(cancellationToken))
            .GroupBy(match => match.AgentId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(match => match.RuleId));

        // Estado atual das labels automaticas do lote — 1 query (antes: 1 por agente).
        var automaticLabelsByAgent = (await _db.AgentLabels
                .Where(label => agentIds.Contains(label.AgentId) && label.SourceType == AgentLabelSourceType.Automatic)
                .ToListAsync(cancellationToken))
            .GroupBy(label => label.AgentId)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Regras habilitadas, restrito aos ids que estamos avaliando. Antes a consulta
        // varria a tabela inteira de regras a cada lote; agora e indexada pelos ids do
        // lote, e reflete desabilitacoes ocorridas entre a preparacao e a gravacao.
        var enabledRuleIds = (await _db.AgentLabelRules
                .AsNoTracking()
                .Where(rule => ruleIds.Contains(rule.Id) && rule.IsEnabled)
                .Select(rule => rule.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var changes = new List<AgentLabelChange>();

        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var agentId = agent.Id;
            hardwareByAgent.TryGetValue(agentId, out var hardware);
            softwareByAgent.TryGetValue(agentId, out var software);
            disksByAgent.TryGetValue(agentId, out var disks);
            customFieldsByAgent.TryGetValue(agentId, out var customFieldValues);

            var softwareList = software ?? [];
            var existingMatches = existingMatchesByAgent.TryGetValue(agentId, out var agentMatches)
                ? agentMatches
                : new Dictionary<Guid, AgentLabelRuleMatch>();

            // Mapa ruleId -> label dos matches EFETIVOS apos esta avaliacao.
            // Precisa ser construido aqui porque:
            //  - matches novos vao para _db, mas nao aparecem no dicionario carregado do banco;
            //  - matches removidos continuam no dicionario (Remove nao o altera), logo usa-lo
            //    diretamente faria a label sobreviver a esta execucao.
            var effectiveLabelsByRule = new Dictionary<Guid, string>();

            foreach (var rule in rules)
            {
                var matched = EvaluateNode(rule.Expression, agent, hardware, softwareList, customFieldValues, disks);
                var hasExistingMatch = existingMatches.TryGetValue(rule.RuleId, out var existing);

                if (matched)
                {
                    if (!hasExistingMatch)
                    {
                        _db.AgentLabelRuleMatches.Add(new AgentLabelRuleMatch
                        {
                            Id = IdGenerator.NewId(),
                            RuleId = rule.RuleId,
                            AgentId = agentId,
                            Label = rule.Label,
                            MatchedAt = now,
                            LastEvaluatedAt = now
                        });
                    }
                    else
                    {
                        // LastEvaluatedAt deve refletir a ultima avaliacao bem-sucedida,
                        // nao apenas a ultima vez que a label mudou (bug de dado na UI).
                        existing!.LastEvaluatedAt = now;
                        if (!string.Equals(existing.Label, rule.Label, StringComparison.OrdinalIgnoreCase))
                            existing.Label = rule.Label;
                    }

                    effectiveLabelsByRule[rule.RuleId] = rule.Label;
                    continue;
                }

                if (!hasExistingMatch)
                    continue;

                if (rule.ApplyMode == AgentLabelApplyMode.ApplyAndRemove)
                {
                    // Nao entra no mapa: a label deve sair nesta execucao.
                    _db.AgentLabelRuleMatches.Remove(existing!);
                }
                else
                {
                    // ApplyOnly preserva o match antigo (semantica de "nao remover").
                    effectiveLabelsByRule[rule.RuleId] = existing!.Label;
                }
            }

            // Deriva as labels efetivas em memoria (sem queries por agente).
            SyncEffectiveLabelsForAgent(
                agentId,
                enabledRuleIds,
                effectiveLabelsByRule,
                automaticLabelsByAgent.TryGetValue(agentId, out var currentLabels) ? currentLabels : [],
                now,
                changes);
        }

        // Um unico SaveChanges por lote (antes: ate 2 por agente).
        await _db.SaveChangesAsync(cancellationToken);

        if (changes.Count > 0)
            await RecordLabelChangesAsync(changes, reason, cancellationToken);

        _logger.LogInformation(
            "Agent auto-labeling evaluated for {AgentCount} agent(s). Reason: {Reason}",
            agents.Count,
            reason);
    }

    /// <summary>
    /// Deriva as labels automaticas efetivas de um agente a partir dos matches em memoria.
    /// Substitui o SyncEffectiveLabelsAsync que fazia 2 queries por agente.
    /// </summary>
    private void SyncEffectiveLabelsForAgent(
        Guid agentId,
        IReadOnlySet<Guid> enabledRuleIds,
        IReadOnlyDictionary<Guid, string> effectiveLabelsByRule,
        IReadOnlyList<AgentLabel> existingAutomaticLabels,
        DateTime now,
        List<AgentLabelChange> changes)
    {
        // Considera apenas regras habilitadas, replicando o join da versao por agente
        // (uma regra desabilitada nao deve manter sua label).
        var shouldKeep = effectiveLabelsByRule
            .Where(entry => enabledRuleIds.Contains(entry.Key))
            .Select(entry => entry.Value)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingSet = existingAutomaticLabels
            .Select(label => label.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var label in shouldKeep)
        {
            if (existingSet.Contains(label))
                continue;

            _db.AgentLabels.Add(new AgentLabel
            {
                Id = IdGenerator.NewId(),
                AgentId = agentId,
                Label = label,
                SourceType = AgentLabelSourceType.Automatic,
                CreatedAt = now,
                UpdatedAt = now
            });
            changes.Add(new AgentLabelChange(agentId, label, AgentLabelSourceType.Automatic, "Added"));
        }

        foreach (var label in existingAutomaticLabels)
        {
            if (shouldKeep.Contains(label.Label))
                continue;

            _db.AgentLabels.Remove(label);
            changes.Add(new AgentLabelChange(agentId, label.Label, AgentLabelSourceType.Automatic, "Removed"));
        }
    }

    private async Task<IReadOnlyList<PreparedRule>> PrepareEnabledRulesAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var rules = await GetCachedEnabledRulesAsync();
        if (rules.Count == 0)
            return [];

        var prepared = new List<PreparedRule>(rules.Count);
        foreach (var rule in rules)
        {
            // Skip manual-mode rules — they are not processed automatically
            if (rule.ApplyMode == AgentLabelApplyMode.Manual)
                continue;

            var expression = TryDeserializeExpression(rule.ExpressionJson);
            if (expression is null)
            {
                _logger.LogWarning("Agent label rule {RuleId} has invalid expression and was skipped.", rule.Id);
                continue;
            }

            prepared.Add(new PreparedRule(rule.Id, rule.Label, rule.ApplyMode, expression));
        }

        return prepared;
    }

    private async Task<IReadOnlyList<AgentLabelRule>> GetCachedEnabledRulesAsync()
    {
        // O cache e uma otimizacao: qualquer falha de Redis degrada para o banco,
        // nunca derruba a avaliacao.
        try
        {
            var cached = await _redisService.GetAsync(EnabledRulesCacheKey);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var deserialized = JsonSerializer.Deserialize<List<EnabledRuleCacheEntry>>(cached, JsonOptions);
                if (deserialized is not null)
                    return deserialized.Select(entry => entry.ToEntity()).ToList();
            }
        }
        catch (JsonException)
        {
            // Payload invalido/contrato antigo: descarta e recarrega do banco.
            try { await _redisService.DeleteAsync(EnabledRulesCacheKey); } catch { /* cache e best-effort */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler o cache de regras de label; consultando o banco.");
        }

        var rules = await _ruleRepository.GetEnabledAsync();

        // Cacheia tambem a lista vazia: antes, uma lista vazia nao era gravada e o
        // valor antigo (stale) continuava sendo servido ate o TTL expirar.
        try
        {
            var payload = JsonSerializer.Serialize(
                rules.Select(EnabledRuleCacheEntry.From).ToList(),
                JsonOptions);
            await _redisService.SetAsync(EnabledRulesCacheKey, payload, EnabledRulesCacheTtlSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar o cache de regras de label.");
        }

        return rules;
    }

    /// <summary>
    /// Projecao enxuta da regra para o cache. Serializar a entidade EF inteira acoplava
    /// o cache ao schema do banco (uma mudanca de coluna invalidava silenciosamente).
    /// </summary>
    private sealed record EnabledRuleCacheEntry(
        Guid Id,
        string Name,
        string Label,
        string? Description,
        bool IsEnabled,
        int ApplyMode,
        string ExpressionJson,
        string? CreatedBy,
        string? UpdatedBy,
        DateTime CreatedAt,
        DateTime UpdatedAt)
    {
        public static EnabledRuleCacheEntry From(AgentLabelRule rule) => new(
            rule.Id, rule.Name, rule.Label, rule.Description, rule.IsEnabled,
            (int)rule.ApplyMode, rule.ExpressionJson, rule.CreatedBy, rule.UpdatedBy,
            rule.CreatedAt, rule.UpdatedAt);

        public AgentLabelRule ToEntity() => new()
        {
            Id = Id,
            Name = Name,
            Label = Label,
            Description = Description,
            IsEnabled = IsEnabled,
            ApplyMode = (AgentLabelApplyMode)ApplyMode,
            ExpressionJson = ExpressionJson,
            CreatedBy = CreatedBy,
            UpdatedBy = UpdatedBy,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    private static AgentLabelRuleExpressionNodeDto? TryDeserializeExpression(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentLabelRuleExpressionNodeDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Carrega os custom fields de varios agentes em 2 queries (valores + definicoes),
    /// agrupando por agente e resolvendo o escopo (Agent/Site/Client) em memoria.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, CustomFieldEntry>>> LoadCustomFieldValuesForAgentsAsync(
        IReadOnlyList<Agent> agents,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<Guid, CustomFieldEntry>>();
        if (agents.Count == 0)
            return result;

        var siteIds = agents.Select(agent => agent.SiteId).Distinct().ToList();
        var sites = await _db.Sites
            .AsNoTracking()
            .Where(site => siteIds.Contains(site.Id))
            .Select(site => new { site.Id, site.ClientId })
            .ToDictionaryAsync(site => site.Id, site => site.ClientId, cancellationToken);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            keys.Add(agent.Id.ToString("D"));
            keys.Add(agent.SiteId.ToString("D"));
            if (sites.TryGetValue(agent.SiteId, out var clientId) && clientId != Guid.Empty)
                keys.Add(clientId.ToString("D"));
        }

        var applicableKeys = keys.ToList();
        var rawValues = await _db.CustomFieldValues
            .AsNoTracking()
            .Where(value => applicableKeys.Contains(value.EntityKey))
            .ToListAsync(cancellationToken);

        if (rawValues.Count == 0)
            return result;

        var definitionIds = rawValues.Select(value => value.DefinitionId).Distinct().ToList();
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id) && definition.IsActive
                && (definition.ScopeType == CustomFieldScopeType.Agent
                    || definition.ScopeType == CustomFieldScopeType.Site
                    || definition.ScopeType == CustomFieldScopeType.Client))
            .ToDictionaryAsync(definition => definition.Id, cancellationToken);

        var valuesByKey = rawValues
            .GroupBy(value => value.EntityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agents)
        {
            var entries = new Dictionary<Guid, CustomFieldEntry>();
            var agentKey = agent.Id.ToString("D");
            var siteKey = agent.SiteId.ToString("D");
            var clientKey = sites.TryGetValue(agent.SiteId, out var clientId) && clientId != Guid.Empty
                ? clientId.ToString("D")
                : null;

            AddCustomFieldsFor(entries, definitions, valuesByKey, agentKey, CustomFieldScopeType.Agent);
            AddCustomFieldsFor(entries, definitions, valuesByKey, siteKey, CustomFieldScopeType.Site);
            if (clientKey is not null)
                AddCustomFieldsFor(entries, definitions, valuesByKey, clientKey, CustomFieldScopeType.Client);

            result[agent.Id] = entries;
        }

        return result;
    }

    private static void AddCustomFieldsFor(
        Dictionary<Guid, CustomFieldEntry> target,
        IReadOnlyDictionary<Guid, CustomFieldDefinition> definitions,
        IReadOnlyDictionary<string, List<CustomFieldValue>> valuesByKey,
        string entityKey,
        CustomFieldScopeType expectedScope)
    {
        if (!valuesByKey.TryGetValue(entityKey, out var values))
            return;

        foreach (var value in values)
        {
            if (!definitions.TryGetValue(value.DefinitionId, out var definition))
                continue;

            // Garante que o valor pertence ao escopo correto da entidade consultada.
            if (definition.ScopeType != expectedScope)
                continue;

            target[value.DefinitionId] = new CustomFieldEntry(value.ValueJson, definition.DataType);
        }
    }

    /// <summary>
    /// Persiste o historico de mudancas de labels automaticas (auditoria).
    /// Nunca derruba a avaliacao — falha apenas e registrada em log.
    /// </summary>
    private async Task RecordLabelChangesAsync(
        IReadOnlyList<AgentLabelChange> changes,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            _logger.LogInformation(
                "Agent label {Action}: agent {AgentId} label {Label} (source={Source}, reason={Reason})",
                change.Action,
                change.AgentId,
                change.Label,
                change.SourceType,
                reason);
        }

        try
        {
            _db.AgentLabelChangeLogs.AddRange(changes.Select(change => new AgentLabelChangeLog
            {
                Id = IdGenerator.NewId(),
                AgentId = change.AgentId,
                Label = change.Label,
                SourceType = change.SourceType,
                Action = change.Action,
                Reason = reason,
                OccurredAt = DateTime.UtcNow
            }));

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao registrar o historico de mudancas de labels de agentes.");
        }
    }

    private static bool HasHardwareConditions(AgentLabelRuleExpressionNodeDto node)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
        {
            return node.Field is AgentLabelField.Processor
                or AgentLabelField.TotalMemoryBytes
                or AgentLabelField.ProcessorCores
                or AgentLabelField.ProcessorThreads
                or AgentLabelField.GpuModel
                or AgentLabelField.GpuMemoryBytes
                or AgentLabelField.MachineScore;
        }

        return node.Children.Any(HasHardwareConditions);
    }

    private static bool HasSoftwareConditions(AgentLabelRuleExpressionNodeDto node)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
        {
            return node.Field is AgentLabelField.SoftwareName
                or AgentLabelField.SoftwarePublisher
                or AgentLabelField.SoftwareVersion
                or AgentLabelField.SoftwareCount;
        }

        return node.Children.Any(HasSoftwareConditions);
    }

    private static bool EvaluateNode(
        AgentLabelRuleExpressionNodeDto node,
        Agent agent,
        AgentHardwareInfo? hardware,
        IReadOnlyCollection<AgentInstalledSoftware> software,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues,
        IReadOnlyCollection<DiskInfo>? disks = null,
        DiskInfo? currentDisk = null)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
            return EvaluateCondition(node, agent, hardware, software, customFieldValues, currentDisk);

        if (node.NodeType == AgentLabelNodeType.DiskGroup)
        {
            if (disks is null || disks.Count == 0)
                return false;

            var logicalOperator = node.LogicalOperator ?? AgentLabelLogicalOperator.And;

            return logicalOperator == AgentLabelLogicalOperator.And
                ? disks.All(dk => node.Children.All(child =>
                    EvaluateNode(child, agent, hardware, software, customFieldValues, disks, dk)))
                : disks.Any(dk => node.Children.All(child =>
                    EvaluateNode(child, agent, hardware, software, customFieldValues, disks, dk)));
        }

        if (node.Children.Count == 0)
            return false;

        var logicalOp = node.LogicalOperator ?? AgentLabelLogicalOperator.And;

        return logicalOp == AgentLabelLogicalOperator.And
            ? node.Children.All(child => EvaluateNode(child, agent, hardware, software, customFieldValues, disks, currentDisk))
            : node.Children.Any(child => EvaluateNode(child, agent, hardware, software, customFieldValues, disks, currentDisk));
    }

    private static bool EvaluateCondition(
        AgentLabelRuleExpressionNodeDto node,
        Agent agent,
        AgentHardwareInfo? hardware,
        IReadOnlyCollection<AgentInstalledSoftware> software,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues,
        DiskInfo? currentDisk)
    {
        if (!node.Field.HasValue || !node.Operator.HasValue)
            return false;

        var op = node.Operator.Value;
        var expected = node.Value ?? string.Empty;

        return node.Field.Value switch
        {
            AgentLabelField.Hostname => EvaluateText(agent.Hostname, op, expected),
            AgentLabelField.DisplayName => EvaluateText(agent.DisplayName, op, expected),
            AgentLabelField.IpAddress => EvaluateText(agent.LastIpAddress, op, expected),
            AgentLabelField.OperatingSystem => EvaluateText(agent.OperatingSystem, op, expected),
            AgentLabelField.OsVersion => EvaluateText(agent.OsVersion, op, expected),
            AgentLabelField.Status => EvaluateText(agent.Status.ToString(), op, expected),
            AgentLabelField.SoftwareName => software.Any(item => EvaluateText(item.Name, op, expected)),
            AgentLabelField.SoftwarePublisher => software.Any(item => EvaluateText(item.Publisher, op, expected)),
            AgentLabelField.SoftwareVersion => software.Any(item => EvaluateText(item.Version, op, expected)),
            AgentLabelField.SoftwareCount => EvaluateNumber((int?)software.Count, op, expected),
            AgentLabelField.Processor => EvaluateText(hardware?.Processor, op, expected),
            AgentLabelField.TotalMemoryBytes => EvaluateNumber(hardware?.TotalMemoryBytes, op, expected),
            AgentLabelField.TotalDisksCount => EvaluateNumber(hardware?.TotalDisksCount, op, expected),
            AgentLabelField.ProcessorCores => EvaluateNumber(hardware?.ProcessorCores, op, expected),
            AgentLabelField.ProcessorThreads => EvaluateNumber(hardware?.ProcessorThreads, op, expected),
            AgentLabelField.GpuModel => EvaluateText(hardware?.GpuModel, op, expected),
            AgentLabelField.GpuMemoryBytes => EvaluateNumber(hardware?.GpuMemoryBytes, op, expected),
            AgentLabelField.DiskDriveLetter => EvaluateText(currentDisk?.DriveLetter, op, expected),
            AgentLabelField.DiskFreeSpaceBytes => EvaluateNumber(currentDisk?.FreeSpaceBytes, op, expected),
            AgentLabelField.DiskTotalSpaceBytes => EvaluateNumber(currentDisk?.TotalSizeBytes, op, expected),
            AgentLabelField.DiskFreeSpacePercent => EvaluateNumber(CalculateDiskFreePercent(currentDisk), op, expected),
            AgentLabelField.DiskFileSystem => EvaluateText(currentDisk?.FileSystem, op, expected),
            AgentLabelField.DiskMediaType => EvaluateText(currentDisk?.MediaType, op, expected),
            AgentLabelField.MachineScore => EvaluateNumber(hardware?.MachineScore, op, expected),
            AgentLabelField.AgentCustomField
                or AgentLabelField.ClientCustomField
                or AgentLabelField.SiteCustomField
                => EvaluateCustomField(node.CustomFieldDefinitionId, op, expected, customFieldValues),
            _ => false
        };
    }

    private static bool EvaluateText(string? current, AgentLabelComparisonOperator op, string expected)
    {
        if (current is null)
            return false;

        return op switch
        {
            AgentLabelComparisonOperator.Contains => current.Contains(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.NotContains => !current.Contains(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.StartsWith => current.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.EndsWith => current.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.Equals => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.NotEquals => !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.Regex => EvaluateRegex(current, expected),
            _ => false
        };
    }

    private static bool EvaluateCustomField(
        Guid? definitionId,
        AgentLabelComparisonOperator op,
        string expected,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues)
    {
        if (!definitionId.HasValue || customFieldValues is null)
            return false;

        if (!customFieldValues.TryGetValue(definitionId.Value, out var entry))
            return false;

        return entry.DataType switch
        {
            CustomFieldDataType.Integer or CustomFieldDataType.Decimal =>
                EvaluateCustomFieldNumeric(entry.ValueJson, op, expected),
            CustomFieldDataType.Boolean =>
                EvaluateCustomFieldBoolean(entry.ValueJson, op, expected),
            CustomFieldDataType.Date or CustomFieldDataType.DateTime =>
                EvaluateCustomFieldDateTime(entry.ValueJson, op, expected),
            _ => // Text, Dropdown, ListBox
                EvaluateCustomFieldText(entry.ValueJson, op, expected)
        };
    }

    private static bool EvaluateCustomFieldText(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string?>(valueJson, JsonOptions);
            return EvaluateText(text, op, expected);
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldNumeric(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var numeric = JsonSerializer.Deserialize<decimal?>(valueJson, JsonOptions);
            if (!numeric.HasValue) return false;
            return EvaluateNumber(numeric.Value, op, expected);
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldBoolean(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var boolValue = JsonSerializer.Deserialize<bool?>(valueJson, JsonOptions);
            if (!boolValue.HasValue) return false;
            var current = boolValue.Value ? "true" : "false";
            return op switch
            {
                AgentLabelComparisonOperator.Equals =>
                    string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                AgentLabelComparisonOperator.NotEquals =>
                    !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldDateTime(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<DateTimeOffset?>(valueJson, JsonOptions);
            if (!stored.HasValue) return false;
            if (!DateTimeOffset.TryParse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expectedDate))
                return false;
            var diff = stored.Value.CompareTo(expectedDate);
            return op switch
            {
                AgentLabelComparisonOperator.Equals => diff == 0,
                AgentLabelComparisonOperator.NotEquals => diff != 0,
                AgentLabelComparisonOperator.GreaterThan => diff > 0,
                AgentLabelComparisonOperator.GreaterThanOrEqual => diff >= 0,
                AgentLabelComparisonOperator.LessThan => diff < 0,
                AgentLabelComparisonOperator.LessThanOrEqual => diff <= 0,
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private async Task<IReadOnlyDictionary<Guid, CustomFieldEntry>> LoadCustomFieldValuesForAgentAsync(
        Guid agentId,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var site = await _siteRepository.GetByIdAsync(siteId);
        var clientId = site?.ClientId;

        var agentKey = agentId.ToString("D");
        var siteKey = siteId.ToString("D");
        var clientKey = clientId?.ToString("D");

        var applicableKeys = clientKey is null
            ? new[] { agentKey, siteKey }
            : new[] { agentKey, siteKey, clientKey };

        var rawValues = await _db.CustomFieldValues
            .AsNoTracking()
            .Where(v => applicableKeys.Contains(v.EntityKey))
            .ToListAsync(cancellationToken);

        if (rawValues.Count == 0)
            return new Dictionary<Guid, CustomFieldEntry>();

        var definitionIds = rawValues.Select(v => v.DefinitionId).Distinct().ToList();
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id) && d.IsActive
                && (d.ScopeType == CustomFieldScopeType.Agent
                    || d.ScopeType == CustomFieldScopeType.Site
                    || d.ScopeType == CustomFieldScopeType.Client))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var result = new Dictionary<Guid, CustomFieldEntry>();
        foreach (var value in rawValues)
        {
            if (!definitions.TryGetValue(value.DefinitionId, out var def))
                continue;

            // scope-aware key match: Agent=agentKey, Site=siteKey, Client=clientKey
            var expectedKey = def.ScopeType switch
            {
                CustomFieldScopeType.Agent => agentKey,
                CustomFieldScopeType.Site => siteKey,
                CustomFieldScopeType.Client => clientKey,
                _ => null
            };

            if (expectedKey is null || !string.Equals(value.EntityKey, expectedKey, StringComparison.OrdinalIgnoreCase))
                continue;

            result[value.DefinitionId] = new CustomFieldEntry(value.ValueJson, def.DataType);
        }

        return result;
    }

    private static bool HasCustomFieldConditions(AgentLabelRuleExpressionNodeDto node)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
        {
            return node.Field is AgentLabelField.AgentCustomField
                or AgentLabelField.ClientCustomField
                or AgentLabelField.SiteCustomField;
        }

        return node.Children.Any(HasCustomFieldConditions);
    }

    private static bool HasDiskConditions(AgentLabelRuleExpressionNodeDto node)
    {
        return node.NodeType switch
        {
            AgentLabelNodeType.DiskGroup => true,
            AgentLabelNodeType.Condition => false,
            _ => node.Children.Any(HasDiskConditions)
        };
    }

    private static decimal? CalculateDiskFreePercent(DiskInfo? disk)
    {
        if (disk is null || disk.TotalSizeBytes == 0)
            return null;
        return (decimal)disk.FreeSpaceBytes / (decimal)disk.TotalSizeBytes * 100m;
    }

    private static bool EvaluateRegex(string current, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        try
        {
            return Regex.IsMatch(current, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool EvaluateNumber(long? current, AgentLabelComparisonOperator op, string expected)
    {
        if (!current.HasValue)
            return false;

        return EvaluateNumber((decimal)current.Value, op, expected);
    }

    private static bool EvaluateNumber(int? current, AgentLabelComparisonOperator op, string expected)
    {
        if (!current.HasValue)
            return false;

        return EvaluateNumber((decimal)current.Value, op, expected);
    }

    private static bool EvaluateNumber(decimal current, AgentLabelComparisonOperator op, string expected)
    {
        if (!decimal.TryParse(expected, out var expectedValue))
            return false;

        return op switch
        {
            AgentLabelComparisonOperator.Equals => current == expectedValue,
            AgentLabelComparisonOperator.NotEquals => current != expectedValue,
            AgentLabelComparisonOperator.GreaterThan => current > expectedValue,
            AgentLabelComparisonOperator.GreaterThanOrEqual => current >= expectedValue,
            AgentLabelComparisonOperator.LessThan => current < expectedValue,
            AgentLabelComparisonOperator.LessThanOrEqual => current <= expectedValue,
            _ => false
        };
    }

    private static bool EvaluateNumber(decimal? current, AgentLabelComparisonOperator op, string expected)
    {
        if (!current.HasValue)
            return false;

        return EvaluateNumber(current.Value, op, expected);
    }
}
