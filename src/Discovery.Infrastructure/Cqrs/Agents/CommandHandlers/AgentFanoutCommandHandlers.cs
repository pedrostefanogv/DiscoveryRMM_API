using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Fanout.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class SendSiteFanoutCommandHandler(
    IAgentMessaging messaging,
    ISiteRepository siteRepo
) : IRequestHandler<SendSiteFanoutCommand, Result<FanoutResponseDto>>
{
    public async Task<Result<FanoutResponseDto>> Handle(SendSiteFanoutCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<FanoutResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var site = await siteRepo.GetByIdAsync(cmd.SiteId);
        if (site is null)
            return Result<FanoutResponseDto>.Failure(Error.NotFound("Site not found."));

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (cmd.ExpiresAtUtc.HasValue && cmd.ExpiresAtUtc.Value <= issuedAtUtc)
            return Result<FanoutResponseDto>.Failure(Error.Validation("ExpiresAtUtc", "expiresAtUtc must be greater than current UTC time."));

        var envelope = FanoutHelper.BuildEnvelope(cmd.CommandType, cmd.Payload, dispatchId, issuedAtUtc, cmd.ExpiresAtUtc, "site", site.ClientId, cmd.SiteId);
        await messaging.PublishSiteFanoutCommandAsync(site.ClientId, cmd.SiteId, envelope, ct);

        return Result<FanoutResponseDto>.Success(new FanoutResponseDto(dispatchId, NatsSubjectBuilder.SiteAgentsCommandSubject(site.ClientId, cmd.SiteId), "site", envelope.IdempotencyKey));
    }
}

public sealed class SendClientFanoutCommandHandler(
    IAgentMessaging messaging
) : IRequestHandler<SendClientFanoutCommand, Result<FanoutResponseDto>>
{
    public async Task<Result<FanoutResponseDto>> Handle(SendClientFanoutCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<FanoutResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (cmd.ExpiresAtUtc.HasValue && cmd.ExpiresAtUtc.Value <= issuedAtUtc)
            return Result<FanoutResponseDto>.Failure(Error.Validation("ExpiresAtUtc", "expiresAtUtc must be greater than current UTC time."));

        var envelope = FanoutHelper.BuildEnvelope(cmd.CommandType, cmd.Payload, dispatchId, issuedAtUtc, cmd.ExpiresAtUtc, "client", cmd.ClientId, null);
        await messaging.PublishClientFanoutCommandAsync(cmd.ClientId, envelope, ct);

        return Result<FanoutResponseDto>.Success(new FanoutResponseDto(dispatchId, NatsSubjectBuilder.ClientAgentsCommandSubject(cmd.ClientId), "client", envelope.IdempotencyKey));
    }
}

public sealed class SendGlobalFanoutCommandHandler(
    IAgentMessaging messaging
) : IRequestHandler<SendGlobalFanoutCommand, Result<FanoutResponseDto>>
{
    public async Task<Result<FanoutResponseDto>> Handle(SendGlobalFanoutCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<FanoutResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (cmd.ExpiresAtUtc.HasValue && cmd.ExpiresAtUtc.Value <= issuedAtUtc)
            return Result<FanoutResponseDto>.Failure(Error.Validation("ExpiresAtUtc", "expiresAtUtc must be greater than current UTC time."));

        var envelope = FanoutHelper.BuildEnvelope(cmd.CommandType, cmd.Payload, dispatchId, issuedAtUtc, cmd.ExpiresAtUtc, "global", null, null);
        await messaging.PublishGlobalFanoutCommandAsync(envelope, ct);

        return Result<FanoutResponseDto>.Success(new FanoutResponseDto(dispatchId, NatsSubjectBuilder.GlobalAgentsCommandSubject(), "global", envelope.IdempotencyKey));
    }
}

internal static class FanoutHelper
{
    internal static CommandDispatchEnvelope BuildEnvelope(string commandType, string? payload, Guid dispatchId, DateTime issuedAtUtc, DateTime? expiresAtUtc, string targetScope, Guid? targetClientId, Guid? targetSiteId)
    {
        var idempotencyKey = $"fanout:{dispatchId:N}";
        return new CommandDispatchEnvelope
        {
            DispatchId = dispatchId,
            CommandType = CommandTypeWireMapper.ToWireValue(Enum.Parse<CommandType>(commandType)),
            TargetScope = targetScope,
            TargetClientId = targetClientId,
            TargetSiteId = targetSiteId,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            IdempotencyKey = idempotencyKey,
            Payload = payload ?? string.Empty,
        };
    }
}