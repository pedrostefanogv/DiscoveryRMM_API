using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/configurations")]
public class ConfigurationsController(
    IMediator mediator,
    IAiModelCatalogService aiCatalog,
    IConfigurationResolver configurationResolver,
    IBackgroundProcessingScheduleService scheduleService,
    DiscoveryDbContext db,
    ILogger<ConfigurationsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string? CurrentUser => HttpContext.Items["Username"] as string;

    /// <summary>
    /// Aplica o tick persistido. Falha aqui NÃO invalida o salvamento da
    /// configuração — apenas fica registrada para a sincronização periódica.
    /// </summary>
    private async Task ApplyScheduleSafeAsync()
    {
        try
        {
            await scheduleService.ApplyAsync(force: false, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao aplicar o agendamento após salvar a configuração.");
        }
    }

    // ── Server ──────────────────────────────────────────────────────────

    [HttpGet("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServer()
    {
        var result = await mediator.Send(new GetServerConfigQuery());
        return result.Match<IActionResult>(
            success: config => Ok(ProjectServerConfigForClient(config)),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("server/metadata")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServerMetadata()
    {
        var server = (await mediator.Send(new GetServerConfigQuery())).Value;
        if (server is null)
            return Ok(new ConfigurationMetadataResult(new(), [], AgentHomeTabCatalog.ValidTabs.ToArray()));

        var result = await mediator.Send(new GetConfigurationMetadataQuery("Server", server.Id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Cópia da configuração do servidor para o cliente web: nunca expõe segredos
    /// (Secret Key do storage e ApiKey/EmbeddingApiKey de IA) — apenas flags de
    /// "configurado", para que o front preserve o valor ao salvar.
    /// </summary>
    private static ServerConfiguration ProjectServerConfigForClient(ServerConfiguration config)
    {
        var clone = JsonSerializer.Deserialize<ServerConfiguration>(
            JsonSerializer.Serialize(config, JsonOptions), JsonOptions) ?? config;

        clone.ObjectStorageSecretKeyConfigured = !string.IsNullOrWhiteSpace(config.ObjectStorageSecretKey);
        clone.ObjectStorageSecretKey = string.Empty;

        var ai = DeserializeAiSettings(clone.AIIntegrationSettingsJson);
        if (ai is not null)
        {
            clone.AiApiKeyConfigured = !string.IsNullOrWhiteSpace(ai.ApiKey);
            ai.ApiKey = null;
            ai.EmbeddingApiKey = null;
            clone.AIIntegrationSettingsJson = JsonSerializer.Serialize(ai, JsonOptions);
        }

        return clone;
    }

    private static AIIntegrationSettings? DeserializeAiSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AIIntegrationSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atualiza a configuração do servidor com semântica de MERGE (patch): apenas as
    /// chaves enviadas são alteradas. Mantido para compatibilidade; antes substituía a
    /// entidade inteira e apagava campos ausentes do payload (bug de perda de dados).
    /// </summary>
    [HttpPut("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateServer([FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchServerConfigCommand(updates, CurrentUser));
        if (result.IsSuccess)
            await ApplyScheduleSafeAsync();

        var payload = result.Match<IActionResult>(
            success: config => Ok(ProjectServerConfigForClient(config)),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
        return payload;
    }

    [HttpPatch("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchServerConfigCommand(updates, CurrentUser));
        if (result.IsSuccess)
            await ApplyScheduleSafeAsync();

        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/reset")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> ResetServer()
    {
        var result = await mediator.Send(new ResetServerConfigCommand(CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Impacto de bloquear campos no servidor: overrides de clientes/sites que a
    /// cascata removeria. Usado para confirmar antes de aplicar locks.
    /// </summary>
    [HttpPost("server/locks/impact")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetLocksImpact([FromBody] LockImpactRequest request)
    {
        var result = await mediator.Send(new GetServerLocksImpactQuery(request.Fields ?? []));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Exporta a configuração do servidor sem segredos.</summary>
    [HttpGet("server/export")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> ExportServer()
    {
        var result = await mediator.Send(new ExportServerConfigurationQuery());
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Importa configuração exportada (merge validado). DryRun só valida as chaves.</summary>
    [HttpPost("server/import")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> ImportServer([FromBody] ServerImportRequest request)
    {
        var result = await mediator.Send(
            new ImportServerConfigurationCommand(request.Settings ?? [], request.DryRun, CurrentUser));

        if (result.IsSuccess)
            await ApplyScheduleSafeAsync();

        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Testa a API key de IA armazenada (write-only) contra o provider.</summary>
    [HttpPost("server/ai/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestStoredAiKey(CancellationToken ct)
    {
        var result = await mediator.Send(new TestStoredAiKeyCommand(), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/object-storage/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestObjectStorage(CancellationToken ct)
    {
        var result = await mediator.Send(new TestObjectStorageCommand(), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/nats/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestNats([FromBody] NatsConnectionTestRequest req, CancellationToken ct)
    {
        var result = await mediator.Send(
            new TestNatsConnectionCommand(req.Url ?? string.Empty, req.User, req.Password), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("server/nats")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchNats([FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchNatsConfigCommand(updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Obtém as configurações globais de anexos de tickets (habilitado, tamanho máximo, tipos permitidos).
    /// </summary>
    [HttpGet("server/ticket-attachments")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetTicketAttachmentSettings()
    {
        var result = await mediator.Send(new GetTicketAttachmentSettingsQuery());
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("server/ticket-attachments")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateTicketAttachmentSettings([FromBody] TicketAttachmentSettings settings)
    {
        var result = await mediator.Send(new UpdateTicketAttachmentSettingsCommand(settings, CurrentUser));
        return result.Match<IActionResult>(
            success: config => Ok(TicketAttachmentSettings.FromJson(config.TicketAttachmentSettingsJson)),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Clients ────────────────────────────────────────────────────────

    [HttpGet("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.View)]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        var result = await mediator.Send(new GetClientConfigQuery(clientId));
        return result.Match<IActionResult>(
            success: c => Ok(c),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateClient(Guid clientId, [FromBody] ClientConfiguration config)
    {
        var result = await mediator.Send(new UpdateClientConfigCommand(clientId, config, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchClientConfigCommand(clientId, updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        await mediator.Send(new DeleteClientConfigCommand(clientId));
        return NoContent();
    }

    // ── Sites ──────────────────────────────────────────────────────────

    [HttpGet("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetSite(Guid siteId)
    {
        var result = await mediator.Send(new GetSiteConfigQuery(siteId));
        return result.Match<IActionResult>(
            success: c => Ok(c),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateSite(Guid siteId, [FromBody] SiteConfiguration config)
    {
        var result = await mediator.Send(new UpdateSiteConfigCommand(siteId, config, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchSite(Guid siteId, [FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchSiteConfigCommand(siteId, updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteSite(Guid siteId)
    {
        await mediator.Send(new DeleteSiteConfigCommand(siteId));
        return NoContent();
    }

    /// <summary>
    /// Resolve a configuração efetiva (merged: server → client → site) para um site específico.
    /// </summary>
    [HttpGet("sites/{siteId:guid}/effective")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetSiteEffective(Guid siteId)
    {
        var result = await mediator.Send(new GetSiteEffectiveConfigQuery(siteId));
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    /// <summary>Configuração efetiva de um cliente (server → client).</summary>
    [HttpGet("clients/{clientId:guid}/effective")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.View)]
    public async Task<IActionResult> GetClientEffective(Guid clientId)
    {
        var result = await mediator.Send(new GetClientEffectiveConfigQuery(clientId));
        return result.Match<IActionResult>(success: Ok, failure: BadRequest);
    }

    /// <summary>Metadados de edição por campo (origem + locks) no escopo do cliente.</summary>
    [HttpGet("clients/{clientId:guid}/metadata")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.View)]
    public async Task<IActionResult> GetClientMetadata(Guid clientId)
        => await SendMetadata("Client", clientId);

    /// <summary>Metadados de edição por campo (origem + locks) no escopo do site.</summary>
    [HttpGet("sites/{siteId:guid}/metadata")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetSiteMetadata(Guid siteId)
        => await SendMetadata("Site", siteId);

    private async Task<IActionResult> SendMetadata(string entityType, Guid entityId)
    {
        var result = await mediator.Send(new GetConfigurationMetadataQuery(entityType, entityId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Processamento em segundo plano (ciclos agendados) ───────────────

    /// <summary>
    /// Configuração efetiva dos ciclos (métricas de atendente e triagem por IA)
    /// para um cliente: global + override do cliente.
    /// </summary>
    [HttpGet("background-processing/effective")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetBackgroundProcessingEffective(
        [FromQuery] Guid? clientId, CancellationToken ct)
    {
        var settings = await configurationResolver.ResolveBackgroundProcessingAsync(clientId, ct);
        return Ok(settings);
    }

    /// <summary>Última execução (e resumo) dos ciclos por escopo de cliente.</summary>
    [HttpGet("background-processing/status")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetBackgroundProcessingStatus(
        [FromQuery] Guid? clientId, CancellationToken ct)
    {
        var query = db.ProcessingScopeStates.AsNoTracking();
        if (clientId.HasValue)
            query = query.Where(state => state.ScopeId == clientId.Value);

        var states = await query
            .OrderBy(state => state.ScopeType)
            .ThenBy(state => state.ScopeId)
            .ToListAsync(ct);

        return Ok(states);
    }

    /// <summary>
    /// Tick aplicado vs desejado por processo, kill switch e próximo disparo.
    /// </summary>
    [HttpGet("background-processing/schedule")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetBackgroundProcessingSchedule(CancellationToken ct)
        => Ok(await scheduleService.GetStatusAsync(ct));

    /// <summary>
    /// Pede o recálculo forçado (backfill) dos snapshots de métricas do escopo
    /// informado (null = global). O trabalho roda em lotes no job de backfill.
    /// </summary>
    [HttpPost("background-processing/backfill")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> RequestBackgroundProcessingBackfill(
        [FromQuery] Guid? clientId,
        [FromQuery] bool purgeOrphans,
        [FromServices] IBackgroundProcessingBackfillService backfillService,
        CancellationToken ct)
    {
        var state = await backfillService.RequestAsync(clientId, purgeOrphans, CurrentUser, ct);
        return Accepted(state);
    }

    /// <summary>Cancela um backfill pendente/em andamento do escopo.</summary>
    [HttpPost("background-processing/backfill/cancel")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> CancelBackgroundProcessingBackfill(
        [FromQuery] Guid? clientId,
        [FromServices] IBackgroundProcessingBackfillService backfillService,
        CancellationToken ct)
    {
        var cancelled = await backfillService.CancelAsync(clientId, CurrentUser, ct);
        return cancelled ? NoContent() : NotFound();
    }

    // ── AI ─────────────────────────────────────────────────────────────

    [HttpGet("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiCredentials(CancellationToken ct)
    {
        var result = await mediator.Send(new GetAiCredentialsQuery(), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> CreateAiCredential([FromBody] AiProviderCredential credential, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateAiCredentialCommand(credential), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("ai/credentials/{credentialId:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteAiCredential(Guid credentialId, CancellationToken ct)
    {
        await mediator.Send(new DeleteAiCredentialCommand(credentialId), ct);
        return NoContent();
    }

    [HttpGet("ai/models")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiModels([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetAiModelsQuery(clientId, siteId, search), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── AI Key Validation ─────────────────────────────────────────────────

    /// <summary>
    /// Valida uma API key contra o provider (faz GET /models com Authorization Bearer).
    /// </summary>
    [HttpPost("ai/validate-key")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> ValidateAiKey([FromBody] AiKeyValidationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { errors = new[] { new { Code = "VALIDATION", Message = "API key é obrigatória." } } });

        var provider = !string.IsNullOrWhiteSpace(request.Provider)
            ? request.Provider
            : AIIntegrationSettings.ProviderOpenRouter;

        var baseUrl = !string.IsNullOrWhiteSpace(request.BaseUrl)
            ? request.BaseUrl
            : provider.ToLowerInvariant() switch
            {
                AIIntegrationSettings.ProviderOpenRouter => AIIntegrationSettings.OpenRouterDefaultBaseUrl,
                _ => AIIntegrationSettings.OpenAiDefaultBaseUrl
            };

        try
        {
            var valid = await aiCatalog.ValidateApiKeyAsync(provider, baseUrl, request.ApiKey, ct);
            return Ok(new { valid, provider });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Ok(new { valid = false, provider, error = "API key não autorizada (401)." });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Ok(new { valid = false, provider, error = "Acesso negado (403). Verifique as permissões da API key." });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao validar API key para provider {Provider}", provider);
            return Ok(new { valid = false, provider, error = $"Erro ao validar: {ex.Message}" });
        }
    }

    // ── OpenRouter Models ──────────────────────────────────────────────────

    /// <summary>
    /// Lista modelos disponíveis diretamente da API OpenRouter (chat, embeddings, rerank).
    /// Cache de 60 minutos, use ?refresh=true para forçar atualização.
    /// </summary>
    [HttpGet("ai/openrouter/models")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetOpenRouterModels(
        [FromQuery] string? modality = null,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        var result = await aiCatalog.ListOpenRouterModelsAsync(modality, refresh, ct);
        return Ok(result);
    }
}

/// <summary>
/// Payload do teste de NATS. Todos os campos são opcionais: a tela envia apenas
/// a URL e as credenciais ficam no servidor. Sem isso o model binding devolvia
/// 400 ("The User/Password field is required") antes de chegar ao validador.
/// </summary>
public record NatsConnectionTestRequest(string? Url, string? User, string? Password);
public record LockImpactRequest(string[] Fields);
public record ServerImportRequest(Dictionary<string, object> Settings, bool DryRun);
