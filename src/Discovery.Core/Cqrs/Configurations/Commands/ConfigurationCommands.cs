using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Cqrs.Configurations.Commands;

public sealed record PatchServerConfigCommand(Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record ResetServerConfigCommand(string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record PatchNatsConfigCommand(Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record UpdateClientConfigCommand(Guid ClientId, ClientConfiguration Config, string? ChangedBy) : ICommand<Result<ClientConfiguration>>;
public sealed record PatchClientConfigCommand(Guid ClientId, Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ClientConfiguration>>;
public sealed record DeleteClientConfigCommand(Guid ClientId) : ICommand<Result<VoidResult>>;
public sealed record UpdateSiteConfigCommand(Guid SiteId, SiteConfiguration Config, string? ChangedBy) : ICommand<Result<SiteConfiguration>>;
public sealed record PatchSiteConfigCommand(Guid SiteId, Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<SiteConfiguration>>;
public sealed record DeleteSiteConfigCommand(Guid SiteId) : ICommand<Result<VoidResult>>;
public sealed record CreateAiCredentialCommand(AiProviderCredential Credential) : ICommand<Result<AiProviderCredential>>;
public sealed record DeleteAiCredentialCommand(Guid CredentialId) : ICommand<Result<VoidResult>>;
public sealed record UpdateTicketAttachmentSettingsCommand(TicketAttachmentSettings Settings, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
/// <summary>Importa configuração do servidor (export/import). DryRun só valida as chaves.</summary>
public sealed record ImportServerConfigurationCommand(Dictionary<string, object> Settings, bool DryRun, string? ChangedBy) : ICommand<Result<ServerConfigurationImportResult>>;

/// <summary>Testa a API key de IA já armazenada (write-only) contra o provider configurado.</summary>
public sealed record TestStoredAiKeyCommand : ICommand<Result<StoredAiKeyTestResult>>;

public sealed record StoredAiKeyTestResult(bool Ok, string? Provider, string? Error);

public sealed record ServerConfigurationImportResult(
    bool DryRun,
    IReadOnlyList<string> AppliedFields,
    IReadOnlyList<string> UnknownFields,
    int Version);

public sealed record TestObjectStorageCommand : ICommand<Result<object>>;
public sealed record TestNatsConnectionCommand(string Url, string User, string Password) : ICommand<Result<NatsConnectionTestResult>>;

public sealed record NatsConnectionTestResult(bool Ok, IReadOnlyList<string> Errors);
