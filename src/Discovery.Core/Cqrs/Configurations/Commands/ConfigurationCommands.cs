using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Configurations.Commands;

public sealed record UpdateServerConfigCommand(ServerConfiguration Config, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record PatchServerConfigCommand(Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record ResetServerConfigCommand(string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record UpdateServerReportingCommand(object Reporting, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record PatchNatsConfigCommand(Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ServerConfiguration>>;
public sealed record UpdateClientConfigCommand(Guid ClientId, ClientConfiguration Config, string? ChangedBy) : ICommand<Result<ClientConfiguration>>;
public sealed record PatchClientConfigCommand(Guid ClientId, Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<ClientConfiguration>>;
public sealed record DeleteClientConfigCommand(Guid ClientId) : ICommand<Result<VoidResult>>;
public sealed record UpdateSiteConfigCommand(Guid SiteId, SiteConfiguration Config, string? ChangedBy) : ICommand<Result<SiteConfiguration>>;
public sealed record PatchSiteConfigCommand(Guid SiteId, Dictionary<string, object> Updates, string? ChangedBy) : ICommand<Result<SiteConfiguration>>;
public sealed record DeleteSiteConfigCommand(Guid SiteId) : ICommand<Result<VoidResult>>;
public sealed record CreateAiCredentialCommand(AiProviderCredential Credential) : ICommand<Result<AiProviderCredential>>;
public sealed record DeleteAiCredentialCommand(Guid CredentialId) : ICommand<Result<VoidResult>>;
public sealed record TestObjectStorageCommand : ICommand<Result<object>>;
public sealed record TestNatsConnectionCommand(string Url, string User, string Password) : ICommand<Result<NatsConnectionTestResult>>;

public sealed record NatsConnectionTestResult(bool Ok, IReadOnlyList<string> Errors);
