using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentRegistration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentRegistration;

/// <summary>
/// Handler para auto-registro de agent via deploy token.
/// Fluxo: valida o deploy token → (opcional) tenta recuperar agent existente pelo fingerprint
/// → cria o Agent no banco (ZeroTouchPending=true) → gera token mdz_ → retorna credenciais.
/// </summary>
public sealed class RegisterAgentFromDeployTokenCommandHandler(
    IDeployTokenService deployTokenService,
    IAgentRepository agentRepo,
    IAgentAuthService agentAuthService,
    ISiteRepository siteRepo,
    IConfigurationService configService,
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

        // 3. Tentar recuperar agent existente pelo fingerprint (Recuperação de Dispositivos)
        var fingerprintHash = ComputeFingerprintHash(cmd.TpmEkHash, cmd.SmbiosUuid);
        var recoveredAgent = await TryRecoverAsync(cmd, clientId, siteId, fingerprintHash, ct);

        Agent agent;
        bool recovered;
        if (recoveredAgent is not null)
        {
            agent = recoveredAgent;
            recovered = true;
            logger.LogInformation(
                "Agent recuperado via fingerprint: AgentId={AgentId}, Hostname={Hostname}, ClientId={ClientId}",
                agent.Id, cmd.Hostname, clientId);
        }
        else
        {
            // 4. Criar o Agent com ZeroTouchPending = true
            agent = new Agent
            {
                SiteId = siteId,
                Hostname = cmd.Hostname,
                DisplayName = cmd.Hostname,
                MacAddress = cmd.MacAddress,
                ZeroTouchPending = true,
                TpmEkHash = cmd.TpmEkHash,
                SmbiosUuid = cmd.SmbiosUuid,
                FingerprintHash = fingerprintHash
            };

            agent = await agentRepo.CreateAsync(agent);
            recovered = false;

            logger.LogInformation(
                "Agent auto-registrado via deploy token: AgentId={AgentId}, Hostname={Hostname}, SiteId={SiteId}, ClientId={ClientId}",
                agent.Id, cmd.Hostname, siteId, clientId);
        }

        // 5. Gerar token mdz_ para o agent (revoga tokens anteriores automaticamente)
        var (agentToken, rawToken) = await agentAuthService.CreateTokenAsync(
            agent.Id,
            $"Auto-registro via deploy token ({deployToken.TokenPrefix}...)");

        logger.LogInformation(
            "Token de agente gerado para AgentId={AgentId}, TokenId={TokenId}",
            agent.Id, agentToken.Id);

        // 6. Retornar credenciais
        return Result<AgentRegistrationResult>.Success(new AgentRegistrationResult(
            Token: rawToken,
            AgentId: agent.Id,
            ClientId: clientId,
            SiteId: agent.SiteId,
            Recovered: recovered
        ));
    }

    /// <summary>
    /// Tenta localizar um agent existente (mesmo cliente, incluindo soft-deleted) cujo fingerprint
    /// coincida. Se encontrado exatamente um, reativa e atualiza os dados. Se houver conflito
    /// (2+ agents), retorna null para que um novo agent seja criado.
    /// </summary>
    private async Task<Agent?> TryRecoverAsync(
        RegisterAgentFromDeployTokenCommand cmd,
        Guid clientId,
        Guid siteId,
        string? fingerprintHash,
        CancellationToken ct)
    {
        // Sem fingerprint → não há como recuperar.
        if (string.IsNullOrWhiteSpace(fingerprintHash))
            return null;

        // Recovery deve estar habilitado (Server > Client > Site).
        var serverConfig = await configService.GetServerConfigAsync();
        var clientConfig = await configService.GetClientConfigAsync(clientId);
        var siteConfig = await configService.GetSiteConfigAsync(siteId);
        var recoveryEnabled = siteConfig?.RecoveryEnabled ?? clientConfig?.RecoveryEnabled ?? serverConfig.RecoveryEnabled;
        if (!recoveryEnabled)
            return null;

        var matches = await agentRepo.FindByFingerprintAsync(fingerprintHash, clientId, ct);

        // Conflito (2+ agents com o mesmo fingerprint) → não recuperar, criar novo.
        if (matches.Count != 1)
            return null;

        var existing = matches[0];

        // Reativa (limpa soft-delete) e atualiza dados básicos + fingerprint.
        existing.DeletedAt = null;
        existing.ZeroTouchPending = false;
        existing.Hostname = cmd.Hostname;
        existing.DisplayName = cmd.Hostname;
        existing.MacAddress = cmd.MacAddress;
        existing.SiteId = siteId;
        existing.TpmEkHash = cmd.TpmEkHash;
        existing.SmbiosUuid = cmd.SmbiosUuid;
        existing.FingerprintHash = fingerprintHash;

        await agentRepo.UpdateAsync(existing);
        return existing;
    }

    /// <summary>
    /// Calcula o hash combinado do fingerprint. Prioriza TPM EK; usa SMBIOS UUID como fallback.
    /// Retorna null se nenhum dos dois estiver disponível.
    /// </summary>
    internal static string? ComputeFingerprintHash(string? tpmEkHash, string? smbiosUuid)
    {
        var tpm = Normalize(tpmEkHash);
        var uuid = Normalize(smbiosUuid);

        if (string.IsNullOrEmpty(tpm) && string.IsNullOrEmpty(uuid))
            return null;

        var combined = $"{tpm}|{uuid}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
