using System.Text.Json.Nodes;
using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Cqrs.Configurations.Queries;

public sealed record GetServerConfigQuery : IQuery<Result<ServerConfiguration>>;
public sealed record GetClientConfigQuery(Guid ClientId) : IQuery<Result<ClientConfiguration>>;
public sealed record GetSiteConfigQuery(Guid SiteId) : IQuery<Result<SiteConfiguration>>;
public sealed record GetAiCredentialsQuery : IQuery<Result<IReadOnlyList<AiProviderCredential>>>;
public sealed record GetAiModelsQuery(Guid? ClientId, Guid? SiteId, string? Search) : IQuery<Result<object>>;

/// <summary>
/// Resolve a configuração efetiva (merged: server → client → site) para um site específico.
/// </summary>
public sealed record GetSiteEffectiveConfigQuery(Guid SiteId) : IQuery<Result<ResolvedConfiguration>>;

/// <summary>
/// Resolve a configuração efetiva (merged: server → client) para um cliente específico.
/// </summary>
public sealed record GetClientEffectiveConfigQuery(Guid ClientId) : IQuery<Result<ResolvedConfiguration>>;

/// <summary>
/// Metadados de edição por campo (origem efetiva + locks) para governança de herança.
/// EntityType: "Server" | "Client" | "Site".
/// </summary>
public sealed record GetConfigurationMetadataQuery(string EntityType, Guid EntityId) : IQuery<Result<ConfigurationMetadataResult>>;

/// <summary>
/// Obtém as configurações globais de anexos de tickets (TicketAttachmentSettings).
/// </summary>
public sealed record GetTicketAttachmentSettingsQuery : IQuery<Result<TicketAttachmentSettings>>;

/// <summary>Impacto de bloquear campos no servidor: overrides que seriam removidos.</summary>
public sealed record GetServerLocksImpactQuery(string[] Fields) : IQuery<Result<ConfigurationLockImpact>>;

/// <summary>Exporta a configuração do servidor (sem segredos) para backup/promoção.</summary>
public sealed record ExportServerConfigurationQuery : IQuery<Result<ServerConfigurationExport>>;

public sealed record ConfigurationLockImpactField(string Field, int Clients, int Sites);

public sealed record ConfigurationLockImpact(
    IReadOnlyList<ConfigurationLockImpactField> Fields,
    int AffectedClients,
    int AffectedSites);

public sealed record ServerConfigurationExport(
    string SchemaVersion,
    string ExportedAt,
    int ConfigurationVersion,
    JsonObject Settings);

/// <summary>Metadados de um campo para o editor (origem e bloqueios).</summary>
public sealed record ConfigurationFieldMetadataResult(
    int? SourceType,
    bool CanEditAtClient,
    bool CanEditAtSite,
    string? LockOwnerForClient,
    string? LockOwnerForSite);

/// <summary>Resposta de metadados de edição de configuração.</summary>
/// <param name="AgentHomeTabOptions">
/// Valores aceitos para a página inicial do agent (agentHomeTab). Fonte de verdade
/// no servidor (AgentHomeTabCatalog) para o console renderizar as opções sem
/// duplicar o contrato.
/// </param>
public sealed record ConfigurationMetadataResult(
    Dictionary<string, ConfigurationFieldMetadataResult> Fields,
    string[] BlockedFields,
    string[] AgentHomeTabOptions);
