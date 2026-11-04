using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// CRUD e resolução de escopo das definições de sensor. A validação vive aqui
/// (mesmo padrão de AutomationTaskService): controllers traduzem a exceção.
/// </summary>
public sealed class SensorDefinitionService(
    DiscoveryDbContext db,
    IAgentLabelRepository labelRepository,
    IAgentRepository agentRepository,
    ISiteRepository siteRepository) : ISensorDefinitionService
{
    /// <summary>Teto de itens por página na listagem (protege a resposta).</summary>
    public const int MaxPageSize = 500;

    /// <summary>Tamanho de página usado quando o cliente não informa.</summary>
    public const int DefaultPageSize = 100;

    private static readonly HashSet<SensorType> ThresholdRequiredTypes =
    [
        SensorType.DiskSpace, SensorType.Cpu, SensorType.Memory, SensorType.Uptime,
        SensorType.Ping, SensorType.HttpProbe, SensorType.HttpThroughput,
        SensorType.DnsLatency, SensorType.Bandwidth, SensorType.DiskIo,
        // Fase 5: contagem de eventos e validade de certificado exigem limiar.
        SensorType.EventLogRate, SensorType.CertificateExpiry
    ];

    public async Task<SensorDefinitionPageDto> ListAsync(
        AppApprovalScopeType? scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        bool includeInactive,
        string? search = null,
        SensorType? sensorType = null,
        bool? isActive = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.SensorDefinitions.AsNoTracking().Where(s => s.DeletedAt == null);
        if (!includeInactive) query = query.Where(s => s.IsActive);
        if (scopeType is not null) query = query.Where(s => s.ScopeType == scopeType);
        if (clientId is not null) query = query.Where(s => s.ClientId == clientId);
        if (siteId is not null) query = query.Where(s => s.SiteId == siteId);
        if (agentId is not null) query = query.Where(s => s.AgentId == agentId);
        if (sensorType is not null) query = query.Where(s => s.SensorType == sensorType);
        // Filtro explícito de ativo/inativo: só faz sentido junto de includeInactive,
        // senão o filtro base já restringiu a ativos.
        if (isActive is not null) query = query.Where(s => s.IsActive == isActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Busca case-insensitive em nome/chave/descrição/alvo. Usa ToLower()
            // (traduz para lower() no Postgres) em vez de ILike: o ILike do
            // provider Npgsql não é traduzível quando o provedor é outro (ex.:
            // InMemory nos testes) e a query cai para avaliação em cliente.
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(s =>
                s.Name.ToLower().Contains(term)
                || (s.Key != null && s.Key.ToLower().Contains(term))
                || (s.Description != null && s.Description.ToLower().Contains(term))
                || (s.Target != null && s.Target.ToLower().Contains(term)));
        }

        // Total ANTES do recorte: o cliente precisa saber que existe mais coisa
        // (antes a listagem cortava em 500 itens silenciosamente).
        var total = await query.CountAsync(cancellationToken);

        var size = pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize;
        var currentPage = page is > 0 ? page.Value : 1;

        var items = await query
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var scriptNames = await LoadScriptNamesAsync(items, cancellationToken);
        var scopeNames = await LoadScopeNamesAsync(items, cancellationToken);
        var dtos = items.Select(s => ToDto(s, scriptNames, scopeNames: scopeNames)).ToList();

        return new SensorDefinitionPageDto
        {
            Items = dtos,
            Count = dtos.Count,
            Total = total,
            Page = currentPage,
            PageSize = size
        };
    }

    public async Task<SensorDefinitionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await db.SensorDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null, cancellationToken);
        if (definition is null) return null;

        var scriptNames = await LoadScriptNamesAsync([definition], cancellationToken);
        var scopeNames = await LoadScopeNamesAsync([definition], cancellationToken);
        return ToDto(definition, scriptNames, scopeNames: scopeNames);
    }

    public async Task<SensorDefinitionDto> CreateAsync(SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default)
    {
        var definition = new SensorDefinition
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actor
        };

        ApplyRequest(definition, request, isCreate: true);
        await ValidateAsync(definition, cancellationToken);

        db.SensorDefinitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);

        var scriptNames = await LoadScriptNamesAsync([definition], cancellationToken);
        var scopeNames = await LoadScopeNamesAsync([definition], cancellationToken);
        return ToDto(definition, scriptNames, scopeNames: scopeNames);
    }

    public async Task<SensorDefinitionDto?> UpdateAsync(Guid id, SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default)
    {
        var definition = await db.SensorDefinitions
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null, cancellationToken);
        if (definition is null) return null;

        // PUT: os campos "nuláveis por natureza" (limiares, script, canais,
        // extratores, exit map, chart) são SUBSTITUÍDOS (null limpa); os demais
        // escalares só mudam quando informados.
        ApplyRequest(definition, request, isCreate: false);
        definition.UpdatedAt = DateTime.UtcNow;
        definition.LastUpdatedAt = DateTime.UtcNow;

        await ValidateAsync(definition, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var scriptNames = await LoadScriptNamesAsync([definition], cancellationToken);
        var scopeNames = await LoadScopeNamesAsync([definition], cancellationToken);
        return ToDto(definition, scriptNames, scopeNames: scopeNames);
    }

    public async Task<bool> DeleteAsync(Guid id, string? actor, CancellationToken cancellationToken = default)
    {
        var definition = await db.SensorDefinitions
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null, cancellationToken);
        if (definition is null) return false;

        // Soft delete: preserva estado/histórico do agente e libera a chave.
        definition.IsActive = false;
        definition.DeletedAt = DateTime.UtcNow;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void ApplyRequest(SensorDefinition definition, SensorUpsertRequest request, bool isCreate)
    {
        if (!string.IsNullOrWhiteSpace(request.Name)) definition.Name = request.Name.Trim();
        if (request.Description is not null) definition.Description = request.Description;
        // PUT: o tipo é sempre o informado (o DTO tem default Script para criação).
        definition.SensorType = request.SensorType;

        definition.ScopeType = request.ScopeType;
        definition.ClientId = request.ScopeType == AppApprovalScopeType.Client ? request.ClientId : null;
        definition.SiteId = request.ScopeType == AppApprovalScopeType.Site ? request.SiteId : null;
        definition.AgentId = request.ScopeType == AppApprovalScopeType.Agent ? request.AgentId : null;

        if (request.IncludeTags is not null) definition.IncludeTagsJson = SensorJson.WriteTags(request.IncludeTags);
        if (request.ExcludeTags is not null) definition.ExcludeTagsJson = SensorJson.WriteTags(request.ExcludeTags);

        if (request.Target is not null) definition.Target = string.IsNullOrWhiteSpace(request.Target) ? null : request.Target.Trim();
        definition.WarningThreshold = request.WarningThreshold;
        definition.CriticalThreshold = request.CriticalThreshold;
        if (request.ThresholdUnit is not null) definition.ThresholdUnit = request.ThresholdUnit.Value;
        if (request.Comparison is not null) definition.Comparison = request.Comparison.Value;

        if (request.OptionsJson is not null) definition.OptionsJson = NormalizeJsonOrNull(request.OptionsJson);
        definition.ScriptId = request.ScriptId;

        if (request.Channels is not null) definition.ChannelsJson = SensorJson.WriteChannels(request.Channels);
        if (request.ExtractorsJson is not null) definition.ExtractorsJson = NormalizeJsonOrNull(request.ExtractorsJson);
        if (request.ExitCodeMapJson is not null) definition.ExitCodeMapJson = NormalizeJsonOrNull(request.ExitCodeMapJson);
        if (request.ChartJson is not null) definition.ChartJson = NormalizeJsonOrNull(request.ChartJson);

        if (request.IntervalSeconds is not null) definition.IntervalSeconds = request.IntervalSeconds.Value;
        if (request.TimeoutSeconds is not null) definition.TimeoutSeconds = request.TimeoutSeconds.Value;
        if (request.StoreSamples is not null) definition.StoreSamples = request.StoreSamples.Value;
        if (request.FailsBeforeAlert is not null) definition.FailsBeforeAlert = request.FailsBeforeAlert.Value;
        if (request.SustainSamples is not null) definition.SustainSamples = request.SustainSamples.Value;
        if (request.MinSeverityToAlert is not null) definition.MinSeverityToAlert = request.MinSeverityToAlert.Value;
        if (request.SeverityWhenFailing is not null) definition.SeverityWhenFailing = request.SeverityWhenFailing.Value;
        if (request.AlertOnSensorError is not null) definition.AlertOnSensorError = request.AlertOnSensorError.Value;

        if (request.DisplayOrder is not null) definition.DisplayOrder = request.DisplayOrder.Value;
        if (request.IsPinned is not null) definition.IsPinned = request.IsPinned.Value;
        if (request.IsActive is not null) definition.IsActive = request.IsActive.Value;
        if (request.Key is not null) definition.Key = NormalizeKey(request.Key);
        else if (isCreate && definition.Key is null && !string.IsNullOrWhiteSpace(definition.Name))
            definition.Key = NormalizeKey(definition.Name);

        definition.LastUpdatedAt = DateTime.UtcNow;
    }

    private static string? NormalizeJsonOrNull(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : json.Trim();

    public static string? NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') builder.Append(ch);
            else if (ch is ' ' or '.') builder.Append('-');
        }

        var key = builder.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(key) ? null : key[..Math.Min(key.Length, 100)];
    }

    private async Task ValidateAsync(SensorDefinition definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new SensorValidationException("Nome do sensor é obrigatório.");
        if (definition.Name.Length > 200)
            throw new SensorValidationException("Nome do sensor deve ter no máximo 200 caracteres.");

        if (!Enum.IsDefined(definition.SensorType))
            throw new SensorValidationException("Tipo de sensor inválido.");

        ValidateScope(definition);

        if (definition.IntervalSeconds != 0
            && (definition.IntervalSeconds < SensorDefaults.MinIntervalSeconds || definition.IntervalSeconds > SensorDefaults.MaxIntervalSeconds))
        {
            throw new SensorValidationException(
                $"Intervalo deve ser 0 (herdar) ou estar entre {SensorDefaults.MinIntervalSeconds} e {SensorDefaults.MaxIntervalSeconds} segundos.");
        }

        if (definition.TimeoutSeconds is < 5 or > 3600)
            throw new SensorValidationException("Timeout deve estar entre 5 e 3600 segundos.");

        if (definition.FailsBeforeAlert is < 1 or > 100)
            throw new SensorValidationException("Falhas antes de alertar deve estar entre 1 e 100.");

        if (definition.SustainSamples is < 1 or > 50)
            throw new SensorValidationException("Amostras para escalar severidade deve estar entre 1 e 50.");

        if (definition.DisplayOrder is < 0 or > 99)
            throw new SensorValidationException("Ordem de exibição deve estar entre 0 e 99.");

        ValidateThresholds(definition);
        await ValidateScriptAsync(definition, cancellationToken);
        ValidateExtractors(definition);
        ValidateExitCodeMap(definition);
        await ValidateKeyUniquenessAsync(definition, cancellationToken);
    }

    private static void ValidateScope(SensorDefinition definition)
    {
        switch (definition.ScopeType)
        {
            case AppApprovalScopeType.Client when definition.ClientId is null:
                throw new SensorValidationException("Escopo Cliente exige clientId.");
            case AppApprovalScopeType.Site when definition.SiteId is null:
                throw new SensorValidationException("Escopo Site exige siteId.");
            case AppApprovalScopeType.Agent when definition.AgentId is null:
                throw new SensorValidationException("Escopo Agente exige agentId.");
        }
    }

    private static void ValidateThresholds(SensorDefinition definition)
    {
        if (definition.WarningThreshold is null && definition.CriticalThreshold is null)
        {
            if (ThresholdRequiredTypes.Contains(definition.SensorType))
                throw new SensorValidationException("Informe o limiar de atenção e/ou crítico.");
            return;
        }

        if (definition.ThresholdUnit == SensorChannelUnit.Percent)
        {
            if (definition.WarningThreshold is < 0 or > 100 || definition.CriticalThreshold is < 0 or > 100)
                throw new SensorValidationException("Limiares em percentual devem estar entre 0 e 100.");
        }

        if (definition.WarningThreshold is double warning && definition.CriticalThreshold is double critical)
        {
            var invalid = definition.Comparison == SensorComparison.LowerIsWorse
                ? warning <= critical
                : warning >= critical;

            if (invalid)
            {
                throw new SensorValidationException(definition.Comparison == SensorComparison.LowerIsWorse
                    ? "Com 'quanto menor, pior' o limiar de atenção deve ser MAIOR que o crítico (ex.: atenção 15%, crítico 10%)."
                    : "Com 'quanto maior, pior' o limiar de atenção deve ser MENOR que o crítico (ex.: atenção 85%, crítico 95%).");
            }
        }

        var booleanPrimaryTypes = new[]
        {
            SensorType.Service, SensorType.Process, SensorType.BitLocker
        };

        if (booleanPrimaryTypes.Contains(definition.SensorType))
        {
            throw new SensorValidationException(
                "Sensores com canal primário booleano (serviço/processo/BitLocker) não usam limiares numéricos.");
        }
    }

    private async Task ValidateScriptAsync(SensorDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.SensorType != SensorType.Script)
        {
            definition.ScriptId = null;
            return;
        }

        var hasInline = ExtractInlineContent(definition.OptionsJson) is not null;
        if (definition.ScriptId is not null && hasInline)
            throw new SensorValidationException("Escolha um script do catálogo OU um script inline (não os dois).");

        if (definition.ScriptId is null && !hasInline)
            throw new SensorValidationException("Sensor de script exige um script do catálogo ou conteúdo inline.");

        if (definition.ScriptId is Guid scriptId)
        {
            var exists = await db.AutomationScriptDefinitions.AsNoTracking()
                .AnyAsync(s => s.Id == scriptId && s.IsActive, cancellationToken);
            if (!exists)
                throw new SensorValidationException("Script informado não existe ou está inativo.");
        }

        var channels = SensorJson.ParseChannels(definition.ChannelsJson);
        if (channels.Count == 0)
            throw new SensorValidationException("Sensor de script exige ao menos um canal de métrica declarado.");
    }

    private static string? ExtractInlineContent(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("inlineContent", out var content) || content.ValueKind != JsonValueKind.String)
                return null;
            var text = content.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateExtractors(SensorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ExtractorsJson)) return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(definition.ExtractorsJson);
        }
        catch (JsonException)
        {
            throw new SensorValidationException("Extratores devem ser um JSON válido.");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new SensorValidationException("Extratores devem ser uma lista JSON.");

            var declared = SensorJson.ParseChannels(definition.ChannelsJson)
                .Select(c => c.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new SensorValidationException("Cada extrator deve ser um objeto JSON.");

                var channel = item.TryGetProperty("channel", out var channelElement) && channelElement.ValueKind == JsonValueKind.String
                    ? channelElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(channel))
                    throw new SensorValidationException("Extrator sem 'channel'.");
                if (declared.Count > 0 && !declared.Contains(channel!))
                    throw new SensorValidationException($"Extrator referencia canal não declarado: {channel}.");

                var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()?.ToLowerInvariant()
                    : "regex";

                if (type == "regex")
                {
                    var pattern = item.TryGetProperty("regex", out var regexElement) && regexElement.ValueKind == JsonValueKind.String
                        ? regexElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(pattern))
                        throw new SensorValidationException($"Extrator do canal {channel} sem 'regex'.");
                    try
                    {
                        _ = new Regex(pattern!, RegexOptions.Compiled, TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is ArgumentException or RegexParseException)
                    {
                        throw new SensorValidationException($"Regex inválida no canal {channel}: {ex.Message}");
                    }
                }
                else if (type == "json")
                {
                    var path = item.TryGetProperty("jsonPath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                        ? pathElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(path))
                        throw new SensorValidationException($"Extrator do canal {channel} sem 'jsonPath'.");
                }
                else if (type != "mdz")
                {
                    throw new SensorValidationException($"Tipo de extrator desconhecido: {type}.");
                }
            }
        }
    }

    private static void ValidateExitCodeMap(SensorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ExitCodeMapJson)) return;

        var rules = SensorExitRules.Parse(definition.ExitCodeMapJson);
        var seen = new HashSet<int>();
        foreach (var rule in rules.Rules)
        {
            foreach (var code in rule.Codes)
            {
                if (!seen.Add(code))
                    throw new SensorValidationException($"Exit code {code} aparece em mais de uma regra.");
            }
        }
    }

    private async Task ValidateKeyUniquenessAsync(SensorDefinition definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Key)) return;

        var duplicated = await db.SensorDefinitions.AsNoTracking()
            .AnyAsync(s => s.Id != definition.Id
                           && s.DeletedAt == null
                           && s.Key == definition.Key
                           && s.ScopeType == definition.ScopeType
                           && s.ClientId == definition.ClientId
                           && s.SiteId == definition.SiteId
                           && s.AgentId == definition.AgentId,
                cancellationToken);

        if (duplicated)
            throw new SensorValidationException($"Já existe um sensor com a chave '{definition.Key}' neste escopo.");
    }

    public async Task<IReadOnlyList<SensorDefinition>> ResolveApplicableAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new SensorValidationException("Agente não encontrado.");

        var clientId = await db.Sites.AsNoTracking()
            .Where(s => s.Id == agent.SiteId)
            .Select(s => (Guid?)s.ClientId)
            .FirstOrDefaultAsync(cancellationToken);

        var labels = await labelRepository.GetByAgentIdAsync(agentId);
        var agentLabels = labels.Select(l => l.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = await db.SensorDefinitions.AsNoTracking()
            .Where(s => s.IsActive && s.DeletedAt == null
                && (s.ScopeType == AppApprovalScopeType.Global
                    || (s.ScopeType == AppApprovalScopeType.Client && clientId != null && s.ClientId == clientId)
                    || (s.ScopeType == AppApprovalScopeType.Site && s.SiteId == agent.SiteId)
                    || (s.ScopeType == AppApprovalScopeType.Agent && s.AgentId == agent.Id)))
            .ToListAsync(cancellationToken);

        var applicable = candidates.Where(s => MatchesTags(s, agentLabels)).ToList();

        // Script sensor cujo script ficou inativo/excluído não é entregue (mesma
        // lógica de FilterDeliverableTasks da automação).
        var scriptIds = applicable
            .Where(s => s.SensorType == SensorType.Script && s.ScriptId.HasValue)
            .Select(s => s.ScriptId!.Value)
            .Distinct()
            .ToList();

        HashSet<Guid> activeScripts = [];
        if (scriptIds.Count > 0)
        {
            activeScripts = (await db.AutomationScriptDefinitions.AsNoTracking()
                    .Where(s => scriptIds.Contains(s.Id) && s.IsActive)
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        return applicable
            .Where(s => s.SensorType != SensorType.Script
                        || (s.ScriptId is null && ExtractInlineContent(s.OptionsJson) is not null)
                        || (s.ScriptId is Guid id && activeScripts.Contains(id)))
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToList();
    }

    private static bool MatchesTags(SensorDefinition definition, HashSet<string> agentLabels)
    {
        var include = SensorJson.ParseTags(definition.IncludeTagsJson);
        if (include.Count > 0 && !include.Any(agentLabels.Contains)) return false;

        var exclude = SensorJson.ParseTags(definition.ExcludeTagsJson);
        return exclude.Count == 0 || !exclude.Any(agentLabels.Contains);
    }

    private async Task<Dictionary<Guid, string>> LoadScriptNamesAsync(IReadOnlyList<SensorDefinition> definitions, CancellationToken cancellationToken)
    {
        var ids = definitions.Where(s => s.ScriptId.HasValue).Select(s => s.ScriptId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.AutomationScriptDefinitions.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => $"{s.Name} v{s.Version}", cancellationToken);
    }

    /// <summary>
    /// Nomes de cliente/site/agente do escopo, resolvidos em lote (uma consulta
    /// por tipo) para a listagem não cair em N+1.
    /// </summary>
    public sealed record SensorScopeNames(
        IReadOnlyDictionary<Guid, string> Clients,
        IReadOnlyDictionary<Guid, string> Sites,
        IReadOnlyDictionary<Guid, string> Agents)
    {
        public static readonly SensorScopeNames Empty = new(
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>());
    }

    private async Task<SensorScopeNames> LoadScopeNamesAsync(
        IReadOnlyList<SensorDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var clientIds = new HashSet<Guid>();
        var siteIds = new HashSet<Guid>();
        var agentIds = new HashSet<Guid>();

        foreach (var definition in definitions)
        {
            if (definition.ClientId is { } clientId) clientIds.Add(clientId);
            if (definition.SiteId is { } siteId) siteIds.Add(siteId);
            if (definition.AgentId is { } agentId) agentIds.Add(agentId);
        }

        if (clientIds.Count == 0 && siteIds.Count == 0 && agentIds.Count == 0)
            return SensorScopeNames.Empty;

        // IMPORTANTE: nada de Task.WhenAll aqui. O DbContext é scoped e NÃO é
        // thread-safe — consultas em paralelo no mesmo contexto estouram
        // "A second operation was started on this context". Consulta em SEQUÊNCIA.
        var agents = new Dictionary<Guid, string>();
        if (agentIds.Count > 0)
        {
            foreach (var agent in await agentRepository.GetByIdsAsync(agentIds.ToList(), cancellationToken))
            {
                agents[agent.Id] = agent.DisplayName ?? agent.Hostname;
                // O agente também revela o site (e o cliente do site) do escopo.
                siteIds.Add(agent.SiteId);
            }
        }

        var sites = new Dictionary<Guid, string>();
        if (siteIds.Count > 0)
        {
            // includeInactive: o nome precisa aparecer mesmo se o site foi desativado.
            foreach (var site in await siteRepository.GetByIdsAsync(siteIds, includeInactive: true))
            {
                sites[site.Id] = site.Name;
                clientIds.Add(site.ClientId);
            }
        }

        var clients = new Dictionary<Guid, string>();
        if (clientIds.Count > 0)
        {
            // IClientRepository não tem lookup em lote (só GetByIdAsync), então
            // consulta em sequência — o conjunto é pequeno (escopos da página).
            foreach (var clientId in clientIds)
            {
                var client = await db.Clients.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
                if (client is not null)
                    clients[clientId] = client.Name;
            }
        }

        return new SensorScopeNames(clients, sites, agents);
    }

    public static SensorDefinitionDto ToDto(
        SensorDefinition definition,
        IReadOnlyDictionary<Guid, string>? scriptNames = null,
        bool isInherited = false,
        AppApprovalScopeType? sourceScope = null,
        SensorScopeNames? scopeNames = null)
        => new()
        {
            Id = definition.Id,
            Key = definition.Key,
            Name = definition.Name,
            Description = definition.Description,
            SensorType = definition.SensorType,
            ScopeType = definition.ScopeType,
            ClientId = definition.ClientId,
            SiteId = definition.SiteId,
            AgentId = definition.AgentId,
            ClientName = definition.ClientId is Guid clientKey && scopeNames is not null && scopeNames.Clients.TryGetValue(clientKey, out var clientName) ? clientName : null,
            SiteName = definition.SiteId is Guid siteKey && scopeNames is not null && scopeNames.Sites.TryGetValue(siteKey, out var siteName) ? siteName : null,
            AgentName = definition.AgentId is Guid agentKey && scopeNames is not null && scopeNames.Agents.TryGetValue(agentKey, out var agentName) ? agentName : null,
            IncludeTags = SensorJson.ParseTags(definition.IncludeTagsJson),
            ExcludeTags = SensorJson.ParseTags(definition.ExcludeTagsJson),
            Target = definition.Target,
            WarningThreshold = definition.WarningThreshold,
            CriticalThreshold = definition.CriticalThreshold,
            ThresholdUnit = definition.ThresholdUnit,
            Comparison = definition.Comparison,
            OptionsJson = definition.OptionsJson,
            ScriptId = definition.ScriptId,
            ScriptName = definition.ScriptId is Guid id && scriptNames is not null && scriptNames.TryGetValue(id, out var name) ? name : null,
            Channels = SensorChannelCatalog.Resolve(definition),
            ExtractorsJson = definition.ExtractorsJson,
            ExitCodeMapJson = definition.ExitCodeMapJson,
            ChartJson = definition.ChartJson,
            IntervalSeconds = SensorDefaults.EffectiveInterval(definition),
            TimeoutSeconds = definition.TimeoutSeconds,
            StoreSamples = definition.StoreSamples,
            FailsBeforeAlert = definition.FailsBeforeAlert,
            SustainSamples = definition.SustainSamples,
            MinSeverityToAlert = definition.MinSeverityToAlert,
            SeverityWhenFailing = definition.SeverityWhenFailing,
            AlertOnSensorError = definition.AlertOnSensorError,
            DisplayOrder = definition.DisplayOrder,
            IsPinned = definition.IsPinned,
            IsActive = definition.IsActive,
            LastUpdatedAt = definition.LastUpdatedAt,
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
            CreatedBy = definition.CreatedBy,
            IsInherited = isInherited,
            SourceScope = sourceScope
        };
}
