using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace Discovery.Infrastructure.Cqrs.Configurations;

// ── Query Handlers ──────────────────────────────────────────────────

public sealed class GetServerConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetServerConfigQuery, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(GetServerConfigQuery q, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.GetServerConfigAsync());
}

public sealed class GetClientConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetClientConfigQuery, Result<ClientConfiguration>>
{
    public async Task<Result<ClientConfiguration>> Handle(GetClientConfigQuery q, CancellationToken ct)
    {
        var c = await config.GetClientConfigAsync(q.ClientId);
        return c is null
            ? Result<ClientConfiguration>.Failure(new[] { Error.NotFound($"Client config for {q.ClientId} not found") })
            : Result<ClientConfiguration>.Success(c);
    }
}

public sealed class GetSiteConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetSiteConfigQuery, Result<SiteConfiguration>>
{
    public async Task<Result<SiteConfiguration>> Handle(GetSiteConfigQuery q, CancellationToken ct)
    {
        var c = await config.GetSiteConfigAsync(q.SiteId);
        return c is null
            ? Result<SiteConfiguration>.Failure(new[] { Error.NotFound($"Site config for {q.SiteId} not found") })
            : Result<SiteConfiguration>.Success(c);
    }
}

public sealed class GetAiCredentialsQueryHandler(IAiProviderCredentialRepository repo)
    : IRequestHandler<GetAiCredentialsQuery, Result<IReadOnlyList<AiProviderCredential>>>
{
    public async Task<Result<IReadOnlyList<AiProviderCredential>>> Handle(GetAiCredentialsQuery q, CancellationToken ct)
        => Result<IReadOnlyList<AiProviderCredential>>.Success(await repo.GetAllAsync(ct));
}

public sealed class GetAiModelsQueryHandler(IAiModelCatalogService catalog)
    : IRequestHandler<GetAiModelsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAiModelsQuery q, CancellationToken ct)
    {
        var models = await catalog.ListModelsAsync(q.ClientId, q.SiteId, new AiModelSearchRequest { Search = q.Search }, ct);
        return Result<object>.Success(models!);
    }
}

// ── Command Handlers ─────────────────────────────────────────────────

public sealed class PatchServerConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<PatchServerConfigCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(PatchServerConfigCommand cmd, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.PatchServerAsync(cmd.Updates, cmd.ChangedBy));
}

public sealed class ResetServerConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<ResetServerConfigCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(ResetServerConfigCommand cmd, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.ResetServerAsync(cmd.ChangedBy));
}

public sealed class PatchNatsConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<PatchNatsConfigCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(PatchNatsConfigCommand cmd, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.PatchServerAsync(cmd.Updates, cmd.ChangedBy));
}

public sealed class UpdateClientConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<UpdateClientConfigCommand, Result<ClientConfiguration>>
{
    public async Task<Result<ClientConfiguration>> Handle(UpdateClientConfigCommand cmd, CancellationToken ct)
        => Result<ClientConfiguration>.Success(await config.UpdateClientAsync(cmd.ClientId, cmd.Config, cmd.ChangedBy));
}

public sealed class PatchClientConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<PatchClientConfigCommand, Result<ClientConfiguration>>
{
    public async Task<Result<ClientConfiguration>> Handle(PatchClientConfigCommand cmd, CancellationToken ct)
        => Result<ClientConfiguration>.Success(await config.PatchClientAsync(cmd.ClientId, cmd.Updates, cmd.ChangedBy));
}

public sealed class DeleteClientConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<DeleteClientConfigCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteClientConfigCommand cmd, CancellationToken ct)
    {
        await config.DeleteClientConfigAsync(cmd.ClientId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class UpdateSiteConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<UpdateSiteConfigCommand, Result<SiteConfiguration>>
{
    public async Task<Result<SiteConfiguration>> Handle(UpdateSiteConfigCommand cmd, CancellationToken ct)
        => Result<SiteConfiguration>.Success(await config.UpdateSiteAsync(cmd.SiteId, cmd.Config, cmd.ChangedBy));
}

public sealed class PatchSiteConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<PatchSiteConfigCommand, Result<SiteConfiguration>>
{
    public async Task<Result<SiteConfiguration>> Handle(PatchSiteConfigCommand cmd, CancellationToken ct)
        => Result<SiteConfiguration>.Success(await config.PatchSiteAsync(cmd.SiteId, cmd.Updates, cmd.ChangedBy));
}

public sealed class DeleteSiteConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<DeleteSiteConfigCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteSiteConfigCommand cmd, CancellationToken ct)
    {
        await config.DeleteSiteConfigAsync(cmd.SiteId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class CreateAiCredentialCommandHandler(IAiProviderCredentialRepository repo)
    : IRequestHandler<CreateAiCredentialCommand, Result<AiProviderCredential>>
{
    public async Task<Result<AiProviderCredential>> Handle(CreateAiCredentialCommand cmd, CancellationToken ct)
        => Result<AiProviderCredential>.Success(await repo.CreateAsync(cmd.Credential, ct));
}

public sealed class DeleteAiCredentialCommandHandler(IAiProviderCredentialRepository repo)
    : IRequestHandler<DeleteAiCredentialCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAiCredentialCommand cmd, CancellationToken ct)
    {
        await repo.DeleteAsync(cmd.CredentialId, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class TestObjectStorageCommandHandler(IObjectStorageProviderFactory factory)
    : IRequestHandler<TestObjectStorageCommand, Result<object>>
{
    public async Task<Result<object>> Handle(TestObjectStorageCommand cmd, CancellationToken ct)
        => Result<object>.Success(await factory.TestConnectionAsync(ct));
}

public sealed class TestNatsConnectionCommandHandler(
    INatsConnectionValidator validator,
    IConfiguration configuration)
    : IRequestHandler<TestNatsConnectionCommand, Result<NatsConnectionTestResult>>
{
    public async Task<Result<NatsConnectionTestResult>> Handle(TestNatsConnectionCommand cmd, CancellationToken ct)
    {
        // Sem credenciais explícitas, testa com as do servidor: é o cenário da
        // tela ("testar o NATS configurado") e o NATS de produção usa auth callout,
        // então uma conexão anônima sempre falharia.
        var user = string.IsNullOrWhiteSpace(cmd.User)
            ? configuration.GetValue<string>("Nats:AuthUser")
            : cmd.User;
        var password = string.IsNullOrWhiteSpace(cmd.Password)
            ? configuration.GetValue<string>("Nats:AuthPassword")
            : cmd.Password;

        var (ok, errors) = await validator.ValidateConnectionAsync(cmd.Url, user, password, ct);
        return Result<NatsConnectionTestResult>.Success(new NatsConnectionTestResult(ok, errors));
    }
}

// ── Effective Config / Ticket Attachments ──────────────────────────

public sealed class GetSiteEffectiveConfigQueryHandler(IConfigurationResolver resolver)
    : IRequestHandler<GetSiteEffectiveConfigQuery, Result<ResolvedConfiguration>>
{
    public async Task<Result<ResolvedConfiguration>> Handle(GetSiteEffectiveConfigQuery q, CancellationToken ct)
    {
        var resolved = await resolver.ResolveForSiteAsync(q.SiteId);
        return Result<ResolvedConfiguration>.Success(resolved);
    }
}

public sealed class GetTicketAttachmentSettingsQueryHandler(IConfigurationService config)
    : IRequestHandler<GetTicketAttachmentSettingsQuery, Result<TicketAttachmentSettings>>
{
    public async Task<Result<TicketAttachmentSettings>> Handle(GetTicketAttachmentSettingsQuery q, CancellationToken ct)
    {
        var server = await config.GetServerConfigAsync();
        var settings = TicketAttachmentSettings.FromJson(server.TicketAttachmentSettingsJson);
        return Result<TicketAttachmentSettings>.Success(settings);
    }
}

public sealed class UpdateTicketAttachmentSettingsCommandHandler(IConfigurationService config)
    : IRequestHandler<UpdateTicketAttachmentSettingsCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(UpdateTicketAttachmentSettingsCommand cmd, CancellationToken ct)
    {
        var errors = cmd.Settings.Validate();
        if (errors.Length > 0)
            return Result<ServerConfiguration>.Failure(
                errors.Select(error => Error.Validation("TicketAttachmentSettingsJson", error)).ToArray());

        var updated = await config.PatchServerAsync(
            new Dictionary<string, object>
            {
                [nameof(ServerConfiguration.TicketAttachmentSettingsJson)] = cmd.Settings.ToJson()
            },
            cmd.ChangedBy);

        return Result<ServerConfiguration>.Success(updated);
    }
}

// ── Effective config (client) / metadata de edição ──────────────────────

public sealed class GetClientEffectiveConfigQueryHandler(IConfigurationResolver resolver)
    : IRequestHandler<GetClientEffectiveConfigQuery, Result<ResolvedConfiguration>>
{
    public async Task<Result<ResolvedConfiguration>> Handle(GetClientEffectiveConfigQuery q, CancellationToken ct)
        => Result<ResolvedConfiguration>.Success(await resolver.ResolveForClientAsync(q.ClientId));
}

/// <summary>
/// Metadados de edição por campo: origem efetiva (server/client/site) + locks de
/// `lockedFieldsJson`. Alimenta os editores de configuração (canEditAtClient/Site).
/// </summary>
public sealed class GetConfigurationMetadataQueryHandler(
    IConfigurationService config,
    IConfigurationResolver resolver,
    ISiteRepository siteRepository)
    : IRequestHandler<GetConfigurationMetadataQuery, Result<ConfigurationMetadataResult>>
{
    private static readonly HashSet<string> AuditProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Version", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy"
    };

    public async Task<Result<ConfigurationMetadataResult>> Handle(
        GetConfigurationMetadataQuery q,
        CancellationToken ct)
    {
        var entityType = (q.EntityType ?? string.Empty).Trim();
        var server = await config.GetServerConfigAsync();
        var serverLocks = ParseLocks(server.LockedFieldsJson);

        var type = entityType.Equals("Client", StringComparison.OrdinalIgnoreCase)
            ? typeof(ClientConfiguration)
            : entityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
                ? typeof(SiteConfiguration)
                : typeof(ServerConfiguration);

        ResolvedConfiguration? resolved = null;
        var clientLocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (type == typeof(ClientConfiguration))
        {
            resolved = await resolver.ResolveForClientAsync(q.EntityId);
            var client = await config.GetClientConfigAsync(q.EntityId);
            clientLocks = ParseLocks(client?.LockedFieldsJson);
        }
        else if (type == typeof(SiteConfiguration))
        {
            resolved = await resolver.ResolveForSiteAsync(q.EntityId);
            var site = await config.GetSiteConfigAsync(q.EntityId);
            var clientId = site?.ClientId ?? (await siteRepository.GetByIdAsync(q.EntityId))?.ClientId;
            var client = clientId.HasValue ? await config.GetClientConfigAsync(clientId.Value) : null;
            clientLocks = ParseLocks(client?.LockedFieldsJson);
        }

        var fields = new Dictionary<string, ConfigurationFieldMetadataResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || AuditProperties.Contains(prop.Name))
                continue;
            if (prop.GetCustomAttribute<NotMappedAttribute>() is not null)
                continue;

            var lockedAtServer = serverLocks.Contains(prop.Name);
            var lockedAtClient = clientLocks.Contains(prop.Name);
            int? sourceType = resolved is null ? null : GetInheritanceSource(resolved, prop.Name);

            fields[ToCamelCase(prop.Name)] = new ConfigurationFieldMetadataResult(
                sourceType,
                CanEditAtClient: !lockedAtServer,
                CanEditAtSite: !lockedAtServer && !lockedAtClient,
                LockOwnerForClient: lockedAtServer ? "Server" : null,
                LockOwnerForSite: lockedAtServer ? "Server" : lockedAtClient ? "Client" : null);
        }

        var blocked = new HashSet<string>(serverLocks, StringComparer.OrdinalIgnoreCase);
        blocked.UnionWith(clientLocks);

        return Result<ConfigurationMetadataResult>.Success(
            new ConfigurationMetadataResult(fields, blocked.OrderBy(x => x).ToArray()));
    }

    private static int GetInheritanceSource(ResolvedConfiguration resolved, string propertyName)
    {
        var key = propertyName switch
        {
            nameof(ServerConfiguration.AIIntegrationSettingsJson) => "AIIntegration",
            nameof(ServerConfiguration.AgentUpdatePolicyJson) => "AgentUpdate",
            nameof(ServerConfiguration.BackgroundProcessingSettingsJson) => "BackgroundProcessing",
            _ => propertyName
        };

        return resolved.Inheritance.TryGetValue(key, out var value)
            ? value
            : (int)ConfigurationPriorityType.Global;
    }

    private static HashSet<string> ParseLocks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json, JsonSerializerOptions.Web) ?? [];
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;

        var upperRun = 0;
        while (upperRun < name.Length && char.IsUpper(name[upperRun]))
            upperRun++;

        if (upperRun == 1)
            return char.ToLowerInvariant(name[0]) + name[1..];

        var take = upperRun - 1;
        return name[..take].ToLowerInvariant() + name[take..];
    }
}

// ── Ferramentas de configuração: locks, export/import e IA ──────────────

/// <summary>
/// Calcula quantos overrides de clientes/sites seriam removidos ao bloquear
/// determinados campos no nível do servidor (o backend remove a cascata).
/// </summary>
public sealed class GetServerLocksImpactQueryHandler(
    IClientConfigurationRepository clientRepo,
    ISiteConfigurationRepository siteRepo)
    : IRequestHandler<GetServerLocksImpactQuery, Result<ConfigurationLockImpact>>
{
    public async Task<Result<ConfigurationLockImpact>> Handle(
        GetServerLocksImpactQuery q,
        CancellationToken ct)
    {
        var fields = (q.Fields ?? [])
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var clients = (await clientRepo.GetAllAsync()).ToList();
        var sites = new List<SiteConfiguration>();
        foreach (var client in clients)
        {
            sites.AddRange(await siteRepo.GetByClientIdAsync(client.ClientId));
        }

        var impacts = new List<ConfigurationLockImpactField>(fields.Length);
        var affectedClientIds = new HashSet<Guid>();
        var affectedSiteIds = new HashSet<Guid>();

        foreach (var field in fields)
        {
            var clientCount = 0;
            foreach (var client in clients)
            {
                if (!HasLocalValue(client, field)) continue;
                clientCount++;
                affectedClientIds.Add(client.ClientId);
            }

            var siteCount = 0;
            foreach (var site in sites)
            {
                if (!HasLocalValue(site, field)) continue;
                siteCount++;
                affectedSiteIds.Add(site.SiteId);
            }

            impacts.Add(new ConfigurationLockImpactField(field, clientCount, siteCount));
        }

        return Result<ConfigurationLockImpact>.Success(
            new ConfigurationLockImpact(impacts, affectedClientIds.Count, affectedSiteIds.Count));
    }

    private static bool HasLocalValue(object target, string field)
    {
        var property = target.GetType().GetProperty(
            field,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        return property is not null && property.GetValue(target) is not null;
    }
}

/// <summary>
/// Exporta a configuração do servidor sem segredos (cópia/backup ou promoção
/// entre ambientes). O import correspondente aplica via merge validado.
/// </summary>
public sealed class ExportServerConfigurationQueryHandler(IConfigurationService config)
    : IRequestHandler<ExportServerConfigurationQuery, Result<ServerConfigurationExport>>
{
    private static readonly HashSet<string> ExclusiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "version", "createdAt", "updatedAt", "createdBy", "updatedBy",
        "objectStorageSecretKeyConfigured", "aiApiKeyConfigured"
    };

    public async Task<Result<ServerConfigurationExport>> Handle(
        ExportServerConfigurationQuery q,
        CancellationToken ct)
    {
        var server = await config.GetServerConfigAsync();

        // Serializa com a política camelCase já usada pela API e remove do payload
        // tudo que não é importável: auditoria, flags de leitura e segredos.
        var all = JsonSerializer.SerializeToNode(server, JsonSerializerOptions.Web)!.AsObject();
        var settings = new JsonObject();

        foreach (var (key, value) in all)
        {
            if (ExclusiveKeys.Contains(key)) continue;
            settings[key] = value?.DeepClone();
        }

        settings["objectStorageSecretKey"] = string.Empty;

        if (settings["aiIntegrationSettingsJson"] is JsonValue aiValue &&
            aiValue.TryGetValue<string>(out var aiJson) &&
            !string.IsNullOrWhiteSpace(aiJson))
        {
            var parsed = JsonSerializer.Deserialize<AIIntegrationSettings>(aiJson, JsonSerializerOptions.Web);
            if (parsed is not null)
            {
                parsed.ApiKey = null;
                parsed.EmbeddingApiKey = null;
                settings["aiIntegrationSettingsJson"] = JsonSerializer.Serialize(parsed, JsonSerializerOptions.Web);
            }
        }

        return Result<ServerConfigurationExport>.Success(new ServerConfigurationExport(
            SchemaVersion: "1",
            ExportedAt: DateTime.UtcNow.ToString("O"),
            ConfigurationVersion: server.Version,
            Settings: settings));
    }
}

/// <summary>
/// Importa configuração exportada. DryRun valida as chaves sem aplicar;
/// a aplicação real passa pelo PATCH (merge + validação + auditoria).
/// </summary>
public sealed class ImportServerConfigurationCommandHandler(IConfigurationService config)
    : IRequestHandler<ImportServerConfigurationCommand, Result<ServerConfigurationImportResult>>
{
    /// <summary>
    /// Campos que existiram e foram removidos do produto. Exports antigos ainda os
    /// trazem; devem ser ignorados em silêncio em vez de virarem erro de "campo
    /// não reconhecido" e quebrarem a restauração de um backup.
    /// </summary>
    private static readonly HashSet<string> RemovedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutoUpdateSettingsJson",
    };

    public async Task<Result<ServerConfigurationImportResult>> Handle(
        ImportServerConfigurationCommand cmd,
        CancellationToken ct)
    {
        var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in cmd.Settings ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            // Import nunca altera segredos write-only.
            if (key.Equals(nameof(ServerConfiguration.ObjectStorageSecretKey), StringComparison.OrdinalIgnoreCase))
                continue;
            // Campo removido do produto: backup antigo, ignora.
            if (RemovedFields.Contains(key))
                continue;
            updates[key] = value;
        }

        var unknown = ConfigurationPatchValidator.FindUnknownFields(typeof(ServerConfiguration), updates.Keys);
        var unknownSet = new HashSet<string>(unknown, StringComparer.OrdinalIgnoreCase);
        var applied = updates.Keys.Where(key => !unknownSet.Contains(key)).ToArray();

        if (cmd.DryRun)
        {
            var current = await config.GetServerConfigAsync();
            return Result<ServerConfigurationImportResult>.Success(
                new ServerConfigurationImportResult(true, applied, unknown, current.Version));
        }

        if (unknown.Count > 0)
        {
            return Result<ServerConfigurationImportResult>.Failure(
                unknown.Select(key => Error.Validation(key, $"Campo não reconhecido: {key}")).ToArray());
        }

        var updated = await config.PatchServerAsync(updates, cmd.ChangedBy);
        return Result<ServerConfigurationImportResult>.Success(
            new ServerConfigurationImportResult(false, applied, [], updated.Version));
    }
}

/// <summary>
/// Valida a API key de IA já armazenada (write-only) contra o provider
/// configurado, permitindo um teste de saúde sem expor o segredo.
/// </summary>
public sealed class TestStoredAiKeyCommandHandler(
    IConfigurationResolver resolver,
    IAiModelCatalogService catalog)
    : IRequestHandler<TestStoredAiKeyCommand, Result<StoredAiKeyTestResult>>
{
    public async Task<Result<StoredAiKeyTestResult>> Handle(
        TestStoredAiKeyCommand cmd,
        CancellationToken ct)
    {
        var ai = await resolver.GetAISettingsAsync();

        if (string.IsNullOrWhiteSpace(ai.ApiKey))
            return Result<StoredAiKeyTestResult>.Success(new StoredAiKeyTestResult(false, ai.Provider, "Nenhuma API key de IA configurada."));

        var baseUrl = !string.IsNullOrWhiteSpace(ai.BaseUrl)
            ? ai.BaseUrl!
            : ai.Provider.ToLowerInvariant() switch
            {
                AIIntegrationSettings.ProviderOpenRouter => AIIntegrationSettings.OpenRouterDefaultBaseUrl,
                _ => AIIntegrationSettings.OpenAiDefaultBaseUrl
            };

        try
        {
            var ok = await catalog.ValidateApiKeyAsync(ai.Provider, baseUrl, ai.ApiKey!, ct);
            return Result<StoredAiKeyTestResult>.Success(
                new StoredAiKeyTestResult(ok, ai.Provider, ok ? null : "API key não autorizada pelo provider."));
        }
        catch (Exception ex)
        {
            return Result<StoredAiKeyTestResult>.Success(
                new StoredAiKeyTestResult(false, ai.Provider, ex.Message));
        }
    }
}
