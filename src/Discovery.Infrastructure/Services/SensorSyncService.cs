using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Sync da configuração de sensores com o agente. Fingerprint próprio (não
/// compartilha a chave da automação) e payload contendo APENAS o que o agente
/// precisa para coletar — limiares e alertas ficam no servidor.
/// </summary>
public sealed class SensorSyncService(
    DiscoveryDbContext db,
    IAgentSensorSyncRepository agentSensorSyncRepository,
    ISensorDefinitionService definitionService) : ISensorSyncService
{
    public async Task<int> GetDefaultIntervalSecondsAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);
        return settings.EffectiveDefaultIntervalSeconds;
    }

    public async Task<string> GetFingerprintAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        // Precisa bater EXATAMENTE com o fingerprint do sync — inclusive com o
        // kill switch ligado/desligado (senão a aba mostraria "agente
        // desatualizado" para sempre quando os sensores estão desligados).
        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);
        var applicable = settings.Enabled
            ? await definitionService.ResolveApplicableAsync(agentId, cancellationToken)
            : [];
        var scripts = await LoadScriptsAsync(applicable, cancellationToken);
        return SensorFingerprint.Compute(applicable, scripts);
    }

    public async Task<AgentSensorSyncResponse> SyncAsync(Guid agentId, AgentSensorSyncRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new SensorValidationException("Agente não encontrado.");

        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);

        // Kill switch: sem sensores na policy o agente simplesmente não coleta.
        var applicable = settings.Enabled
            ? await definitionService.ResolveApplicableAsync(agentId, cancellationToken)
            : [];
        var scripts = await LoadScriptsAsync(applicable, cancellationToken);
        var fingerprint = SensorFingerprint.Compute(applicable, scripts);
        var generatedAt = DateTime.UtcNow;

        // Registra o entregue só quando muda (mesmo racional do policy-sync:
        // escrever a cada sync amplificaria writes sem ganho).
        if (!string.Equals(agent.LastSensorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            agent.LastSensorFingerprint = fingerprint;
            agent.LastSensorSyncAt = generatedAt;
            await agentSensorSyncRepository.SetSensorSyncAsync(agent.Id, fingerprint, generatedAt, cancellationToken);
        }

        var upToDate = !string.IsNullOrWhiteSpace(request.KnownSensorFingerprint)
            && string.Equals(request.KnownSensorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

        if (upToDate && !request.IncludeScriptContent)
        {
            return new AgentSensorSyncResponse
            {
                UpToDate = true,
                SensorFingerprint = fingerprint,
                GeneratedAt = generatedAt,
                SensorCount = applicable.Count,
                DefaultIntervalSeconds = settings.EffectiveDefaultIntervalSeconds,
                Sensors = []
            };
        }

        var policies = applicable.Select(definition => new AgentSensorPolicyDto
        {
            SensorId = definition.Id,
            Key = definition.Key,
            Name = definition.Name,
            SensorType = definition.SensorType,
            Target = definition.Target,
            IntervalSeconds = SensorDefaults.EffectiveInterval(definition),
            TimeoutSeconds = definition.TimeoutSeconds,
            OptionsJson = definition.OptionsJson,
            Channels = SensorJson.ParseChannels(definition.ChannelsJson),
            ExtractorsJson = definition.ExtractorsJson,
            ExitCodeMapJson = definition.ExitCodeMapJson,
            Script = definition.ScriptId is Guid scriptId && scripts.TryGetValue(scriptId, out var script)
                ? new AgentSensorScriptRefDto
                {
                    ScriptId = script.Id,
                    Name = script.Name,
                    Version = script.Version,
                    Summary = script.Summary,
                    ScriptType = script.ScriptType,
                    LastUpdatedAt = script.LastUpdatedAt,
                    ContentHashSha256 = SensorFingerprint.Hash(script.Content),
                    Content = script.Content
                }
                : null
        }).ToList();

        return new AgentSensorSyncResponse
        {
            UpToDate = upToDate,
            SensorFingerprint = fingerprint,
            GeneratedAt = generatedAt,
            SensorCount = policies.Count,
            DefaultIntervalSeconds = settings.EffectiveDefaultIntervalSeconds,
            Sensors = policies
        };
    }

    private async Task<Dictionary<Guid, AutomationScriptDefinition>> LoadScriptsAsync(
        IReadOnlyList<SensorDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var ids = definitions
            .Where(d => d.SensorType == SensorType.Script && d.ScriptId.HasValue)
            .Select(d => d.ScriptId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return [];

        return await db.AutomationScriptDefinitions.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.IsActive)
            .ToDictionaryAsync(s => s.Id, cancellationToken);
    }
}
