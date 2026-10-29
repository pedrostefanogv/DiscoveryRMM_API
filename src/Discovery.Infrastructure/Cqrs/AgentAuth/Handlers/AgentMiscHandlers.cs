using System.Text.Json.Nodes;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentIdentityHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<GetAgentIdentityQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentIdentityQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);

        return Result<object>.Success(new
        {
            agent.Id,
            agent.Hostname,
            agent.DisplayName,
            agent.OperatingSystem,
            agent.OsVersion,
            agent.AgentVersion,
            agent.CommitHash,
            agent.Status,
            ClientId = site?.ClientId,
            agent.SiteId,
            agent.ZeroTouchPending,
            agent.MaintenanceEnabled,
            agent.LastSeenAt
        });
    }
}

public sealed class GetAppStoreEffectiveHandler(
    IAgentRepository agentRepo,
    IConfigurationService configService,
    ISiteRepository siteRepo,
    IAppStoreService appStoreService
) : IRequestHandler<GetAppStoreEffectiveQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAppStoreEffectiveQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var serverConfig = await configService.GetServerConfigAsync();
        ClientConfiguration? clientConfig = null;
        SiteConfiguration? siteConfig = null;
        Guid? clientId = null;
        if (site is not null)
        {
            clientId = site.ClientId;
            clientConfig = await configService.GetClientConfigAsync(site.ClientId);
            siteConfig = await configService.GetSiteConfigAsync(agent.SiteId);
        }

        var policy = siteConfig?.AppStorePolicy ?? clientConfig?.AppStorePolicy ?? serverConfig.AppStorePolicy;
        var enabled = serverConfig.AppStorePolicy != Core.Enums.AppStorePolicyType.Disabled;

        if (!enabled)
            return Result<object>.Success(new
            {
                enabled = false,
                policy = policy.ToString(),
                installationType = q.InstallationType,
                count = 0,
                pageSize = 0,
                hasMore = false,
                nextCursor = "",
                items = Array.Empty<object>()
            });

        // Busca os apps efetivos aprovados para o escopo do agent
        var apps = await appStoreService.GetEffectiveAppsAsync(clientId, agent.SiteId, agent.Id,
            Enum.TryParse<Core.Enums.AppInstallationType>(q.InstallationType, true, out var instType) ? instType : Core.Enums.AppInstallationType.Winget,
            ct);

        // Ordem determinística: o agent reusa o cursor entre requisições, então a
        // lista precisa sair sempre na mesma sequência.
        var ordered = apps
            .OrderBy(a => a.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.InstallationType)
            .ToList();
        var total = ordered.Count;

        var start = 0;
        var effectivePageSize = 0;
        if (q.Paged)
        {
            if (!AppStoreEffectivePagination.TryDecodeCursor(q.Cursor, out start))
                return Result<object>.Failure(Error.Validation("cursor", "Cursor inválido para a paginação da app-store."));
            effectivePageSize = AppStoreEffectivePagination.NormalizePageSize(q.PageSize);
            if (start > total)
                start = total;
        }

        // Modo legado (sem paginação): uma única página com a coleção completa —
        // comportamento preservado para os agents já instalados que enviam apenas
        // pageSize e têm limite de 20 páginas no cliente.
        var end = q.Paged ? Math.Min(start + effectivePageSize, total) : total;
        var slice = ordered.Skip(start).Take(Math.Max(end - start, 0));

        var items = slice.Select(a => new
        {
            installationType = a.InstallationType,
            packageId = a.PackageId,
            name = a.Name,
            description = a.Description,
            iconUrl = a.IconUrl,
            publisher = a.Publisher,
            version = a.Version,
            installCommand = a.InstallCommand,
            uninstallCommand = a.UninstallCommand,
            installCommandKind = a.InstallCommandKind,
            uninstallCommandKind = a.UninstallCommandKind,
            detectionMode = a.DetectionMode,
            inventorySoftwareId = a.InventorySoftwareId,
            detectionName = a.DetectionName,
            detectionPublisher = a.DetectionPublisher,
            detectionMinVersion = a.DetectionMinVersion,
            detectionCommand = a.DetectionCommand,
            extractZip = a.ExtractZip,
            fileName = a.FileName,
            fileSizeBytes = a.FileSizeBytes,
            fileUrl = a.FileUrl,
            fileChecksum = a.FileChecksum,
            silent = a.SilentCommand,
            silentWithProgress = a.SilentWithProgressCommand,
            installerUrlsByArch = a.InstallerUrlsByArch,
            installerTypesByArch = a.InstallerTypesByArch,
            autoUpdateEnabled = a.AutoUpdateEnabled,
            sourceScope = a.SourceScope
        }).ToList();

        var hasMore = q.Paged && end < total;
        var nextCursor = hasMore ? AppStoreEffectivePagination.EncodeCursor(end) : "";

        return Result<object>.Success(new
        {
            enabled,
            policy = policy.ToString(),
            installationType = q.InstallationType,
            // count é o TOTAL da política (não o tamanho da página): é o número
            // que interessa para diagnóstico no agent.
            count = total,
            pageSize = effectivePageSize,
            hasMore,
            nextCursor,
            items
        });
    }
}

public sealed class GetRuntimeCustomFieldsHandler(
    ICustomFieldService customFieldService
) : IRequestHandler<GetRuntimeCustomFieldsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetRuntimeCustomFieldsQuery q, CancellationToken ct)
    {
        var fields = await customFieldService.GetRuntimeValuesForAgentAsync(q.AgentId, q.TaskId, q.ScriptId, ct);

        // O agent Go desserializa este endpoint como um ARRAY de RuntimeCustomField
        // ({definitionId,name,label,scopeType:int,valueJson:<raw>,isSecret}).
        // O wrapper {"fields":[...]} e o enum serializado como string davam
        // "cannot unmarshal object into Go value of type []" — o agent tratava como
        // lista vazia silenciosamente e o MDZ_COLLECT era descartado.
        var payload = fields
            .Select(field => new AgentRuntimeCustomFieldDto(
                field.DefinitionId,
                field.Name,
                field.Label,
                (int)field.ScopeType,
                ParseRuntimeValueJson(field.ValueJson),
                field.IsSecret))
            .ToList();

        return Result<object>.Success(payload);
    }

    private static JsonNode? ParseRuntimeValueJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            return JsonNode.Parse(rawJson);
        }
        catch (System.Text.Json.JsonException)
        {
            // Valor malformado não pode derrubar o runtime inteiro; vai como nulo.
            return null;
        }
    }
}

public sealed class UpsertCollectedCustomFieldHandler(
    ICustomFieldService customFieldService
) : IRequestHandler<UpsertCollectedCustomFieldCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpsertCollectedCustomFieldCommand cmd, CancellationToken ct)
    {
        // O corpo vem plano do agent: {definitionId,name,value,taskId,scriptId}.
        // GetRawText preserva o JSON do valor (string vira "id", número vira número).
        var valueJson = cmd.Request.Value?.GetRawText();
        if (string.IsNullOrWhiteSpace(valueJson))
            return Result<object>.Failure(Error.Validation("customField", "Invalid custom field payload."));

        var input = new Core.DTOs.AgentCustomFieldCollectedValueInput(
            cmd.Request.DefinitionId,
            cmd.Request.Name,
            valueJson,
            cmd.Request.TaskId,
            cmd.Request.ScriptId,
            cmd.Request.UpdatedBy);

        var result = await customFieldService.UpsertAgentCollectedValueAsync(cmd.AgentId, input, ct);
        return Result<object>.Success(new { saved = true, definitionId = result.DefinitionId });
    }
}

public sealed class IssueZeroTouchDeployTokenHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IDeployTokenService deployTokenService
) : IRequestHandler<IssueZeroTouchDeployTokenCommand, Result<object>>
{
    public async Task<Result<object>> Handle(IssueZeroTouchDeployTokenCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<object>.Failure(Error.NotFound("Site not found."));

        var (token, rawToken) = await deployTokenService.CreateZeroTouchTokenAsync(site.ClientId, agent.SiteId);

        return Result<object>.Success(new
        {
            tokenId = token.Id,
            deployKey = rawToken,
            clientId = token.ClientId,
            siteId = token.SiteId,
            // expiresAt (RFC3339) é o campo que o agente Go lê para calcular o TTL da
            // oferta assinada (/p2p/config/onboard → buildOnboardingOffer). Antes o
            // handler só devolvia expiresAtUtc e o agente caía no fallback de 30 min,
            // anunciando uma chave que o servidor já considerava expirada.
            expiresAt = token.ExpiresAt.ToUniversalTime().ToString("O"),
            // expiresAtUtc mantido para compatibilidade com consumidores existentes.
            expiresAtUtc = token.ExpiresAt
        });
    }
}

public sealed class GetAgentUpdateManifestHandler(
    IAgentUpdateService updateService
) : IRequestHandler<GetAgentUpdateManifestQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentUpdateManifestQuery q, CancellationToken ct)
    {
        var manifest = await updateService.GetManifestAsync(q.AgentId, new Core.DTOs.AgentUpdateManifestRequest(
            q.CurrentVersion,
            q.Platform,
            q.Architecture,
            TryParseArtifactType(q.ArtifactType)
        ), ct);

        return Result<object>.Success(manifest);
    }

    private static Core.Enums.AgentReleaseArtifactType? TryParseArtifactType(string? artifactType)
    {
        if (string.IsNullOrWhiteSpace(artifactType)) return null;
        return Enum.TryParse<Core.Enums.AgentReleaseArtifactType>(artifactType, ignoreCase: true, out var result) ? result : null;
    }
}

public sealed class DownloadAgentUpdateHandler(
    IAgentUpdateService updateService
) : IRequestHandler<DownloadAgentUpdateQuery, Result<object>>
{
    public async Task<Result<object>> Handle(DownloadAgentUpdateQuery q, CancellationToken ct)
    {
        var download = await updateService.GetPresignedDownloadUrlAsync(q.AgentId, new Core.DTOs.AgentUpdateDownloadRequest(
            q.ReleaseId,
            q.Version,
            q.Platform,
            q.Architecture,
            Enum.TryParse<Core.Enums.AgentReleaseArtifactType>(q.ArtifactType, ignoreCase: true, out var at) ? at : null
        ), ct);

        if (download is null)
            return Result<object>.Failure(Error.NotFound("No update download available."));

        return Result<object>.Success(download);
    }
}

public sealed class ReportAgentUpdateHandler(
    IAgentUpdateService updateService
) : IRequestHandler<ReportAgentUpdateCommand, Result<object>>
{
    public async Task<Result<object>> Handle(ReportAgentUpdateCommand cmd, CancellationToken ct)
    {
        var eventRecord = await updateService.RecordEventAsync(cmd.AgentId, cmd.Request, ct);
        return Result<object>.Success(eventRecord);
    }
}