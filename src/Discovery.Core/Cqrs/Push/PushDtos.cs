namespace Discovery.Core.Cqrs.Push;

/// <summary>Corpo do registro de inscricao — o usuario vem do token, nunca do corpo.</summary>
public sealed record RegisterPushSubscriptionRequest(
    string Endpoint,
    string P256dh,
    string Auth,
    string? UserAgent = null);

/// <summary>Estado do Web Push para a UI (habilitado, chave publica e inscricoes do usuario).</summary>
public sealed record PushConfigDto(bool Enabled, string VapidPublicKey, int SubscriptionCount);

/// <summary>Inscricao registrada, sem expor chaves/segredos do cliente.</summary>
public sealed record PushSubscriptionDto(Guid Id, string Endpoint, DateTime CreatedAt);
