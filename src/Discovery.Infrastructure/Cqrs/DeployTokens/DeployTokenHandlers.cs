using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.DeployTokens.Commands;
using Discovery.Core.Cqrs.DeployTokens.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.DeployTokens;

public sealed class ListDeployTokensQueryHandler(IDeployTokenRepository repo) : IRequestHandler<ListDeployTokensQuery, Result<IReadOnlyList<DeployTokenDto>>>
{
    public async Task<Result<IReadOnlyList<DeployTokenDto>>> Handle(ListDeployTokensQuery q, CancellationToken ct)
    {
        var tokens = await repo.GetByClientSiteAsync(q.ClientId, q.SiteId);
        var items = tokens.Select(t => new DeployTokenDto(t.Id, t.ClientId, t.SiteId, t.TokenPrefix, null, t.Description, t.CreatedAt, t.ExpiresAt, t.IsRevoked, t.IsExpired, t.UsedCount)).ToList().AsReadOnly();
        return Result<IReadOnlyList<DeployTokenDto>>.Success(items);
    }
}

public sealed class CreateDeployTokenCommandHandler(IDeployTokenService svc, ILoggingService loggingService) : IRequestHandler<CreateDeployTokenCommand, Result<DeployTokenDto>>
{
    public async Task<Result<DeployTokenDto>> Handle(CreateDeployTokenCommand cmd, CancellationToken ct)
    {
        var (token, rawToken) = await svc.CreateTokenAsync(cmd.ClientId, cmd.SiteId, cmd.Description, cmd.ExpiresInHours, cmd.MultiUse);

        await loggingService.LogInfoAsync(
            LogType.Agent,
            LogSource.Api,
            $"deploy.token.created",
            new { tokenId = token.Id, cmd.ClientId, cmd.SiteId, multiUse = cmd.MultiUse, expiresInHours = cmd.ExpiresInHours },
            clientId: cmd.ClientId.ToString(),
            siteId: cmd.SiteId.ToString(),
            cancellationToken: ct);

        return Result<DeployTokenDto>.Success(new DeployTokenDto(token.Id, token.ClientId, token.SiteId, token.TokenPrefix, rawToken, token.Description, token.CreatedAt, token.ExpiresAt, token.IsRevoked, token.IsExpired, 0));
    }
}

public sealed class CreateDeployTokenAndDownloadHandler(
    IDeployTokenService deployTokenService,
    IAgentPackageService agentPackageService,
    ILoggingService loggingService,
    ILogger<CreateDeployTokenAndDownloadHandler> logger)
    : IRequestHandler<CreateDeployTokenAndDownloadCommand, Result<DeployTokenDownloadResult>>
{
    public async Task<Result<DeployTokenDownloadResult>> Handle(CreateDeployTokenAndDownloadCommand cmd, CancellationToken ct)
    {
        var (token, rawToken) = await deployTokenService.CreateTokenAsync(
            cmd.ClientId, cmd.SiteId, cmd.Description, cmd.ExpiresInHours, cmd.MultiUse);

        await loggingService.LogInfoAsync(
            LogType.Agent,
            LogSource.Api,
            "deploy.token.created_for_download",
            new { tokenId = token.Id, cmd.ClientId, cmd.SiteId, multiUse = cmd.MultiUse, installerType = cmd.InstallerType },
            clientId: cmd.ClientId.ToString(),
            siteId: cmd.SiteId.ToString(),
            cancellationToken: ct);

        try
        {
            if (string.Equals(cmd.InstallerType, "offline", StringComparison.OrdinalIgnoreCase))
            {
                var zipBytes = await agentPackageService.BuildPortablePackageAsync(rawToken, cancellationToken: ct);
                logger.LogInformation("Portable package generated for deploy token prefix={Prefix}", token.TokenPrefix);
                return Result<DeployTokenDownloadResult>.Success(new DeployTokenDownloadResult(
                    zipBytes, "discovery-installer-offline.zip", "application/zip"));
            }

            // Default: online = bootstrap (minimal) installer
            var (content, fileName) = await agentPackageService.BuildBootstrapInstallerAsync(rawToken, cancellationToken: ct);
            logger.LogInformation("Bootstrap installer generated: {FileName} ({Size} bytes) for deploy token prefix={Prefix}",
                fileName, content.Length, token.TokenPrefix);
            return Result<DeployTokenDownloadResult>.Success(new DeployTokenDownloadResult(
                content, fileName, "application/vnd.microsoft.portable-executable"));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Failed to build installer for deploy token prefix={Prefix}", token.TokenPrefix);
            return Result<DeployTokenDownloadResult>.Failure(
                Error.Internal("Instalador indisponível temporariamente. Tente novamente em instantes."));
        }
    }
}

public sealed class RevokeDeployTokenCommandHandler(IDeployTokenService svc, ILoggingService loggingService) : IRequestHandler<RevokeDeployTokenCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeDeployTokenCommand cmd, CancellationToken ct)
    {
        await svc.RevokeTokenAsync(cmd.TokenId);

        await loggingService.LogInfoAsync(
            LogType.Agent,
            LogSource.Api,
            "deploy.token.revoked",
            new { tokenId = cmd.TokenId },
            cancellationToken: ct);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
