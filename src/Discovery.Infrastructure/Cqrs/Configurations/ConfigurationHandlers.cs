using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Configurations;

// ── Query Handlers ──────────────────────────────────────────────────

public sealed class GetServerConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetServerConfigQuery, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(GetServerConfigQuery q, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.GetServerConfigAsync());
}

public sealed class GetClientConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetClientConfigQuery, Result<ClientConfiguration?>>
{
    public async Task<Result<ClientConfiguration?>> Handle(GetClientConfigQuery q, CancellationToken ct)
        => Result<ClientConfiguration?>.Success(await config.GetClientConfigAsync(q.ClientId));
}

public sealed class GetSiteConfigQueryHandler(IConfigurationService config)
    : IRequestHandler<GetSiteConfigQuery, Result<SiteConfiguration?>>
{
    public async Task<Result<SiteConfiguration?>> Handle(GetSiteConfigQuery q, CancellationToken ct)
        => Result<SiteConfiguration?>.Success(await config.GetSiteConfigAsync(q.SiteId));
}

public sealed class GetServerReportingQueryHandler(IConfigurationService config)
    : IRequestHandler<GetServerReportingQuery, Result<object?>>
{
    public async Task<Result<object?>> Handle(GetServerReportingQuery q, CancellationToken ct)
    {
        var c = await config.GetServerConfigAsync();
        var result = string.IsNullOrWhiteSpace(c.ReportingSettingsJson)
            ? null
            : JsonSerializer.Deserialize<object>(c.ReportingSettingsJson);
        return Result<object?>.Success(result);
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

public sealed class UpdateServerConfigCommandHandler(IConfigurationService config)
    : IRequestHandler<UpdateServerConfigCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(UpdateServerConfigCommand cmd, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.UpdateServerAsync(cmd.Config, cmd.ChangedBy));
}

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

public sealed class UpdateServerReportingCommandHandler(IConfigurationService config)
    : IRequestHandler<UpdateServerReportingCommand, Result<ServerConfiguration>>
{
    public async Task<Result<ServerConfiguration>> Handle(UpdateServerReportingCommand cmd, CancellationToken ct)
        => Result<ServerConfiguration>.Success(await config.PatchServerAsync(
            new Dictionary<string, object> { ["ReportingSettingsJson"] = JsonSerializer.Serialize(cmd.Reporting) }, cmd.ChangedBy));
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

public sealed class TestNatsConnectionCommandHandler(INatsConnectionValidator validator)
    : IRequestHandler<TestNatsConnectionCommand, Result<NatsConnectionTestResult>>
{
    public async Task<Result<NatsConnectionTestResult>> Handle(TestNatsConnectionCommand cmd, CancellationToken ct)
    {
        var (ok, errors) = await validator.ValidateConnectionAsync(cmd.Url, cmd.User, cmd.Password, ct);
        return Result<NatsConnectionTestResult>.Success(new NatsConnectionTestResult(ok, errors));
    }
}
