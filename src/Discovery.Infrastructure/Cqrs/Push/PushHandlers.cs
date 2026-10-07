using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Push;
using Discovery.Core.Cqrs.Push.Commands;
using Discovery.Core.Cqrs.Push.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Cqrs.Push;

public sealed class RegisterPushSubscriptionCommandHandler(
    IPushSubscriptionRepository repository,
    IOptions<PushOptions> options
) : IRequestHandler<RegisterPushSubscriptionCommand, Result<PushSubscriptionDto>>
{
    public async Task<Result<PushSubscriptionDto>> Handle(RegisterPushSubscriptionCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == Guid.Empty)
            return Result<PushSubscriptionDto>.Failure(Error.Unauthorized("Usuario nao identificado."));

        if (string.IsNullOrWhiteSpace(cmd.Endpoint) ||
            string.IsNullOrWhiteSpace(cmd.P256dh) ||
            string.IsNullOrWhiteSpace(cmd.Auth))
        {
            return Result<PushSubscriptionDto>.Failure(
                Error.Validation("endpoint", "Endpoint, p256dh e auth sao obrigatorios."));
        }

        if (!Uri.TryCreate(cmd.Endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<PushSubscriptionDto>.Failure(
                Error.Validation("endpoint", "O endpoint de push deve ser uma URL HTTPS absoluta."));
        }

        // Limites das colunas: sem isso o INSERT estoura no banco e vira 500.
        if (cmd.Endpoint.Trim().Length > MaxEndpointLength)
        {
            return Result<PushSubscriptionDto>.Failure(
                Error.Validation("endpoint", "Endpoint de push excede o tamanho maximo aceito."));
        }

        if (cmd.P256dh.Trim().Length > MaxKeyLength || cmd.Auth.Trim().Length > MaxKeyLength)
        {
            return Result<PushSubscriptionDto>.Failure(
                Error.Validation("p256dh", "Chaves da inscricao excedem o tamanho maximo aceito."));
        }

        var now = DateTime.UtcNow;
        var saved = await repository.UpsertAsync(new PushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = cmd.UserId,
            Endpoint = cmd.Endpoint.Trim(),
            P256dh = cmd.P256dh.Trim(),
            Auth = cmd.Auth.Trim(),
            UserAgent = Truncate(cmd.UserAgent, 400),
            CreatedAt = now,
            LastSeenAt = now
        }, ct);

        var max = options.Value.MaxSubscriptionsPerUser;
        if (max > 0)
            await repository.TrimAsync(cmd.UserId, max, ct);

        return Result<PushSubscriptionDto>.Success(
            new PushSubscriptionDto(saved.Id, saved.Endpoint, saved.CreatedAt));
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private const int MaxEndpointLength = 1000;
    private const int MaxKeyLength = 255;
}

public sealed class DeletePushSubscriptionCommandHandler(
    IPushSubscriptionRepository repository
) : IRequestHandler<DeletePushSubscriptionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeletePushSubscriptionCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == Guid.Empty)
            return Result<VoidResult>.Failure(Error.Unauthorized("Usuario nao identificado."));

        if (string.IsNullOrWhiteSpace(cmd.Endpoint))
            return Result<VoidResult>.Failure(Error.Validation("endpoint", "Endpoint e obrigatorio."));

        // Idempotente: desinscrever algo que ja nao existe e sucesso.
        await repository.DeleteByEndpointAsync(cmd.UserId, cmd.Endpoint.Trim(), ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class GetPushStatusQueryHandler(
    IWebPushSender sender,
    IPushSubscriptionRepository repository
) : IRequestHandler<GetPushStatusQuery, Result<PushConfigDto>>
{
    public async Task<Result<PushConfigDto>> Handle(GetPushStatusQuery q, CancellationToken ct)
    {
        var count = q.UserId == Guid.Empty
            ? 0
            : await repository.CountByUserIdAsync(q.UserId, ct);

        return Result<PushConfigDto>.Success(new PushConfigDto(sender.IsConfigured, sender.PublicKey, count));
    }
}
