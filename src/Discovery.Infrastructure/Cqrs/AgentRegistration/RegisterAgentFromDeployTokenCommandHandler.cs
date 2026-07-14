using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentRegistration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentRegistration;

/// <summary>
/// Handler para auto-registro de agent via deploy token.
/// Fluxo: valida o deploy token → cria o Agent no banco (ZeroTouchPending=true)
/// → gera token mdz_ → retorna credenciais para o agent.
/// </summary>
public sealed class RegisterAgentFromDeployTokenCommandHandler(
    IDeployTokenService deployTokenService,
    IAgentRepository agentRepo,
    IAgentAuthService agentAuthService,
    ISiteRepository siteRepo,
    ILogger<RegisterAgentFromDeployTokenCommandHandler> logger
) : IRequestHandler<RegisterAgentFromDeployTokenCommand, Result<AgentRegistrationResult>>
{
    public async Task<Result<AgentRegistrationResult>> Handle(
        RegisterAgentFromDeployTokenCommand cmd, CancellationToken ct)
    {
        // 1. Validar o deploy token e consumi-lo (uso único)
        var deployToken = await deployTokenService.TryUseTokenAsync(cmd.DeployToken);
        if (deployToken is null)
            return Result<AgentRegistrationResult>.Failure(
                [Error.Unauthorized("Deploy token inválido, expirado ou já utilizado.")]);

        if (!deployToken.ClientId.HasValue || !deployToken.SiteId.HasValue)
            return Result<AgentRegistrationResult>.Failure(
                [Error.Unauthorized("Deploy token sem escopo de cliente/site.")]);

        var clientId = deployToken.ClientId.Value;
        var siteId = deployToken.SiteId.Value;

        // 2. Validar que o site existe e pertence ao cliente do token
        var site = await siteRepo.GetByIdAsync(siteId);
        if (site is null || site.ClientId != clientId)
            return Result<AgentRegistrationResult>.Failure(
                [Error.NotFound("Site não encontrado ou não pertence ao cliente do token.")]);

        // 3. Criar o Agent com ZeroTouchPending = true
        var agent = new Agent
        {
            SiteId = siteId,
            Hostname = cmd.Hostname,
            DisplayName = cmd.Hostname,
            MacAddress = cmd.MacAddress,
            ZeroTouchPending = true
        };

        var created = await agentRepo.CreateAsync(agent);

        logger.LogInformation(
            "Agent auto-registrado via deploy token: AgentId={AgentId}, Hostname={Hostname}, SiteId={SiteId}, ClientId={ClientId}",
            created.Id, cmd.Hostname, siteId, clientId);

        // 4. Gerar token mdz_ para o agent
        var (agentToken, rawToken) = await agentAuthService.CreateTokenAsync(
            created.Id,
            $"Auto-registro via deploy token ({deployToken.TokenPrefix}...)");

        logger.LogInformation(
            "Token de agente gerado para AgentId={AgentId}, TokenId={TokenId}",
            created.Id, agentToken.Id);

        // 5. Retornar credenciais
        return Result<AgentRegistrationResult>.Success(new AgentRegistrationResult(
            Token: rawToken,
            AgentId: created.Id,
            ClientId: clientId,
            SiteId: siteId
        ));
    }
}
