using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve o nome de EXIBICAO do usuario (User.FullName) para o chat de
/// suporte. Os claims do JWT carregam o login ("pedrostefanogv"), nao o nome;
/// gravar isso em StartedByName/AuthorName fazia o usuario final ver o
/// username do tecnico em vez do nome.
///
/// Cache em memoria com TTL curto: as mensagens do chat consultam o nome a
/// cada envio e a consulta em banco por mensagem seria N+1. Nomes mudam
/// raramente; TTL de minutos e suficiente (a proxima mensagem ja ve o nome
/// atualizado). A API roda como instancia unica atras do nginx — se virar
/// multi-instancia, trocar por cache compartilhado mantendo a interface.
/// </summary>
public sealed class ChatUserNameResolver : IChatUserNameResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IUserRepository _userRepository;
    private readonly ILogger<ChatUserNameResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public ChatUserNameResolver(
        IUserRepository userRepository,
        ILogger<ChatUserNameResolver> logger,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<string> ResolveAsync(Guid? userId, string fallback, CancellationToken cancellationToken = default)
    {
        fallback = (fallback ?? string.Empty).Trim();
        if (userId is not { } id || id == Guid.Empty)
            return FallbackOr(fallback);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(id, out var entry) && now < entry.ExpiresAt)
        {
            // Cache negativo: usuario sem nome no cadastro nao precisa de nova
            // consulta a cada mensagem.
            return entry.DisplayName is { } cached ? cached : FallbackOr(fallback);
        }

        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            var displayName = Normalize(user?.FullName) ?? Normalize(user?.Login);
            _cache[id] = new CacheEntry(displayName, now + (displayName is null ? NegativeCacheTtl : CacheTtl));
            return displayName ?? FallbackOr(fallback);
        }
        catch (Exception ex)
        {
            // Nome e cosmético: nunca bloquear o envio/abertura da conversa por
            // uma falha de leitura do cadastro.
            _logger.LogWarning(ex, "ChatUserNameResolver: falha ao resolver nome do usuario {UserId}; usando fallback", id);
            return FallbackOr(fallback);
        }
    }

    private static string FallbackOr(string fallback)
        => fallback.Length > 0 ? fallback : "suporte";

    private static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record CacheEntry(string? DisplayName, DateTimeOffset ExpiresAt);
}
