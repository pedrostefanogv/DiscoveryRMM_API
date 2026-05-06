using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de comando em massa (fan-out) por escopo site/client/global.
/// </summary>
public partial class AgentsController
{
    [HttpPost("commands/fanout/site/{siteId:guid}")]
    public async Task<IActionResult> SendFanoutCommandToSite(Guid siteId, [FromBody] SendFanoutCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (!_messaging.IsConnected)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "NATS realtime transport unavailable." });

        if (string.IsNullOrWhiteSpace(request.Payload))
            return BadRequest(new { error = "Payload is required." });

        var site = await _siteRepository.GetByIdAsync(siteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc.Value <= issuedAtUtc)
            return BadRequest(new { error = "expiresAtUtc must be greater than current UTC time." });

        var envelope = BuildDispatchEnvelope(
            request,
            dispatchId,
            issuedAtUtc,
            targetScope: "site",
            targetClientId: site.ClientId,
            targetSiteId: site.Id);

        await _messaging.PublishSiteFanoutCommandAsync(site.ClientId, site.Id, envelope, cancellationToken);

        return Accepted(new FanoutDispatchResponse(
            DispatchId: dispatchId,
            Subject: NatsSubjectBuilder.SiteAgentsCommandSubject(site.ClientId, site.Id),
            TargetScope: "site",
            TargetClientId: site.ClientId,
            TargetSiteId: site.Id,
            IssuedAtUtc: issuedAtUtc,
            ExpiresAtUtc: envelope.ExpiresAtUtc,
            IdempotencyKey: envelope.IdempotencyKey));
    }

    [HttpPost("commands/fanout/client/{clientId:guid}")]
    public async Task<IActionResult> SendFanoutCommandToClient(Guid clientId, [FromBody] SendFanoutCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (!_messaging.IsConnected)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "NATS realtime transport unavailable." });

        if (string.IsNullOrWhiteSpace(request.Payload))
            return BadRequest(new { error = "Payload is required." });

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc.Value <= issuedAtUtc)
            return BadRequest(new { error = "expiresAtUtc must be greater than current UTC time." });

        var envelope = BuildDispatchEnvelope(
            request,
            dispatchId,
            issuedAtUtc,
            targetScope: "client",
            targetClientId: clientId,
            targetSiteId: null);

        await _messaging.PublishClientFanoutCommandAsync(clientId, envelope, cancellationToken);

        return Accepted(new FanoutDispatchResponse(
            DispatchId: dispatchId,
            Subject: NatsSubjectBuilder.ClientAgentsCommandSubject(clientId),
            TargetScope: "client",
            TargetClientId: clientId,
            TargetSiteId: null,
            IssuedAtUtc: issuedAtUtc,
            ExpiresAtUtc: envelope.ExpiresAtUtc,
            IdempotencyKey: envelope.IdempotencyKey));
    }

    [HttpPost("commands/fanout/global")]
    public async Task<IActionResult> SendFanoutCommandGlobal([FromBody] SendFanoutCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (!_messaging.IsConnected)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "NATS realtime transport unavailable." });

        if (string.IsNullOrWhiteSpace(request.Payload))
            return BadRequest(new { error = "Payload is required." });

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        if (request.ExpiresAtUtc.HasValue && request.ExpiresAtUtc.Value <= issuedAtUtc)
            return BadRequest(new { error = "expiresAtUtc must be greater than current UTC time." });

        var envelope = BuildDispatchEnvelope(
            request,
            dispatchId,
            issuedAtUtc,
            targetScope: "global",
            targetClientId: null,
            targetSiteId: null);

        await _messaging.PublishGlobalFanoutCommandAsync(envelope, cancellationToken);

        return Accepted(new FanoutDispatchResponse(
            DispatchId: dispatchId,
            Subject: NatsSubjectBuilder.GlobalAgentsCommandSubject(),
            TargetScope: "global",
            TargetClientId: null,
            TargetSiteId: null,
            IssuedAtUtc: issuedAtUtc,
            ExpiresAtUtc: envelope.ExpiresAtUtc,
            IdempotencyKey: envelope.IdempotencyKey));
    }

    private static CommandDispatchEnvelope BuildDispatchEnvelope(
        SendFanoutCommandRequest request,
        Guid dispatchId,
        DateTime issuedAtUtc,
        string targetScope,
        Guid? targetClientId,
        Guid? targetSiteId)
    {
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"fanout:{dispatchId:N}"
            : request.IdempotencyKey.Trim();

        return new CommandDispatchEnvelope
        {
            DispatchId = dispatchId,
            CommandId = request.CommandId,
            CommandType = CommandTypeWireMapper.ToWireValue(request.CommandType),
            TargetScope = targetScope,
            TargetClientId = targetClientId,
            TargetSiteId = targetSiteId,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc,
            IdempotencyKey = idempotencyKey,
            Payload = request.Payload,
        };
    }
}

public record SendFanoutCommandRequest(
    CommandType CommandType,
    string Payload,
    DateTime? ExpiresAtUtc = null,
    string? IdempotencyKey = null,
    Guid? CommandId = null);

public record FanoutDispatchResponse(
    Guid DispatchId,
    string Subject,
    string TargetScope,
    Guid? TargetClientId,
    Guid? TargetSiteId,
    DateTime IssuedAtUtc,
    DateTime? ExpiresAtUtc,
    string IdempotencyKey);
