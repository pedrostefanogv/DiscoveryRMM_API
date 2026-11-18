using System.Security.Cryptography;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Autenticação baseada em token para agents.
/// Gera tokens aleatórios, armazena hash SHA-256 no banco.
/// Preparado para ser substituído/complementado por mTLS.
/// </summary>
public class AgentTokenAuthService : IAgentAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxTokenCacheTtlSeconds = 600;

    private readonly IAgentTokenRepository _tokenRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly IRedisService _redisService;

    public AgentTokenAuthService(
        IAgentTokenRepository tokenRepo,
        IAgentRepository agentRepo,
        IRedisService redisService)
    {
        _tokenRepo = tokenRepo;
        _agentRepo = agentRepo;
        _redisService = redisService;
    }

    public async Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found");

        // Regra de negocio: manter um unico token ativo por agent.
        var activeTokens = (await _tokenRepo.GetByAgentIdAsync(agent.Id))
            .Where(token => token.RevokedAt is null)
            .ToList();

        if (activeTokens.Count > 0)
        {
            await _tokenRepo.RevokeAllByAgentIdAsync(agent.Id);
            foreach (var activeToken in activeTokens)
            {
                if (!string.IsNullOrWhiteSpace(activeToken.TokenHash))
                    await _redisService.DeleteAsync(GetTokenCacheKey(activeToken.TokenHash));
            }
        }

        var rawToken = GenerateRawToken();
        var tokenHash = HashToken(rawToken);
        var prefix = rawToken[..8];

        var token = new AgentToken
        {
            Id = IdGenerator.NewId(),
            AgentId = agentId,
            TokenHash = tokenHash,
            TokenPrefix = prefix,
            Description = description,
            ExpiresAt = null,
            CreatedAt = DateTime.UtcNow
        };

        await _tokenRepo.CreateAsync(token);
        await CacheTokenAsync(token);
        return (token, rawToken);
    }

    public async Task<AgentToken?> ValidateTokenAsync(string rawToken)
    {
        var hash = HashToken(rawToken);
        var cacheKey = GetTokenCacheKey(hash);
        var token = await TryGetCachedTokenAsync(cacheKey);

        if (token is null)
        {
            token = await _tokenRepo.GetByTokenHashAsync(hash);
            if (token is not null)
                await CacheTokenAsync(token);
        }

        if (token is null || !token.IsValid)
        {
            await _redisService.DeleteAsync(cacheKey);
            return null;
        }

        // Agents SOFT-DELETED (lixeira) continuam autenticáveis de propósito: o
        // token é a credencial que permite ao servidor mandar o comando de
        // desinstalação e o agent concluir o descomissionamento — o
        // AgentAuthMiddleware recorta o acesso (403 em tudo exceto
        // DELETE /agent-auth/me). O GetByIdAsync aplica o query filter global e
        // DEVOLVIA null para agente na lixeira, o que REVOGAVA o token e
        // devolvia 401 antes do 403 do middleware. Usar a leitura sem filtro:
        // só revoga quando o agent NÃO existe de verdade (hard delete).
        var agent = await _agentRepo.GetByIdForDecommissionAsync(token.AgentId);
        if (agent is null)
        {
            await _tokenRepo.RevokeAsync(token.Id);
            await _redisService.DeleteAsync(cacheKey);
            return null;
        }

        await _tokenRepo.UpdateLastUsedAsync(token.Id);
        token.LastUsedAt = DateTime.UtcNow;
        await CacheTokenAsync(token);
        return token;
    }

    public async Task RevokeTokenAsync(Guid tokenId)
    {
        var token = await _tokenRepo.GetByIdAsync(tokenId);
        await _tokenRepo.RevokeAsync(tokenId);

        if (!string.IsNullOrWhiteSpace(token?.TokenHash))
            await _redisService.DeleteAsync(GetTokenCacheKey(token.TokenHash));
    }

    public async Task RevokeAllTokensAsync(Guid agentId)
    {
        var tokens = (await _tokenRepo.GetByAgentIdAsync(agentId)).ToList();
        await _tokenRepo.RevokeAllByAgentIdAsync(agentId);

        foreach (var token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token.TokenHash))
                await _redisService.DeleteAsync(GetTokenCacheKey(token.TokenHash));
        }
    }

    public async Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId)
    {
        return await _tokenRepo.GetByAgentIdAsync(agentId);
    }

    // ── Fase 1: Detecção de conexão duplicada via Redis ───────────────────────

    /// <summary>TTL da trava de sessão NATS. Após este tempo sem renovação, a sessão é considerada expirada.</summary>
    private static readonly TimeSpan DefaultNatsSessionTtl = TimeSpan.FromMinutes(3);

    public async Task<bool> TryAcquireNatsSessionAsync(Guid tokenId, Guid agentId, string userNkey, TimeSpan sessionTtl)
    {
        if (sessionTtl <= TimeSpan.Zero)
            sessionTtl = DefaultNatsSessionTtl;

        var sessionKey = GetNatsSessionKey(tokenId);
        var sessionValue = System.Text.Json.JsonSerializer.Serialize(new
        {
            agentId = agentId.ToString(),
            userNkey,
            acquiredAt = DateTime.UtcNow.ToString("O")
        });
        var expirySeconds = (int)Math.Ceiling(sessionTtl.TotalSeconds);

        // Tenta adquirir via SET NX (primeira conexão)
        var acquired = await _redisService.SetIfNotExistsAsync(sessionKey, sessionValue, expirySeconds);
        if (acquired)
            return true;

        // Sessão já existe — verifica quem a segura:
        //   - MESMO agente + MESMO user_nkey: renova a sessão (reconnect do
        //     mesmo cliente, ex.: troca de transporte nats→wss);
        //   - MESMO agente + user_nkey DIFERENTE: conexão DUPLICADA usando o
        //     mesmo token — REJEITA (era aqui que o token "clonado" passava:
        //     a sobrescrita silenciosamente rouba a sessão da conexão original);
        //   - Agente diferente: token está sendo usado por outro agente → rejeitar.
        var existing = await _redisService.GetAsync(sessionKey);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            try
            {
                var existingData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(existing);
                var sameAgent = existingData.TryGetProperty("agentId", out var existingAgentId)
                    && existingAgentId.GetString() == agentId.ToString();
                var sameUserNkey = existingData.TryGetProperty("userNkey", out var existingUserNkey)
                    && string.Equals(existingUserNkey.GetString(), userNkey, StringComparison.Ordinal);

                if (sameAgent && sameUserNkey)
                {
                    // Renova a sessão do mesmo cliente
                    await _redisService.SetAsync(sessionKey, sessionValue, expirySeconds);
                    return true;
                }
            }
            catch { /* fall through to rejection */ }
        }

        return false;
    }

    public async Task ReleaseNatsSessionAsync(Guid tokenId)
    {
        var sessionKey = GetNatsSessionKey(tokenId);
        await _redisService.DeleteAsync(sessionKey);
    }

    // ── Fase 2: Auditoria de última conexão NATS ──────────────────────────────

    public async Task UpdateLastNatsConnectedAsync(Guid tokenId)
    {
        await _tokenRepo.UpdateLastNatsConnectedAsync(tokenId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetNatsSessionKey(Guid tokenId) => $"nats:session:token:{tokenId}";

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"mdz_{Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    internal static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<AgentToken?> TryGetCachedTokenAsync(string cacheKey)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (string.IsNullOrWhiteSpace(cached))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentToken>(cached, JsonOptions);
        }
        catch (JsonException)
        {
            await _redisService.DeleteAsync(cacheKey);
            return null;
        }
    }

    private async Task CacheTokenAsync(AgentToken token)
    {
        if (string.IsNullOrWhiteSpace(token.TokenHash))
            return;

        var effectiveTtl = MaxTokenCacheTtlSeconds;
        var payload = JsonSerializer.Serialize(token, JsonOptions);
        await _redisService.SetAsync(GetTokenCacheKey(token.TokenHash), payload, effectiveTtl);
    }

    private static string GetTokenCacheKey(string tokenHash) => $"token:hash:{tokenHash}";
}
