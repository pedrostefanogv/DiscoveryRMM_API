using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Sites.PowerManagement.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Sites.CommandHandlers;

/// <summary>
/// Envia comando de reinicialização em massa para todos os agentes online do site,
/// via fan-out no subject tenant.{c}.site.{s}.agents.command.
/// </summary>
public sealed class SiteRestartCommandHandler(
    IAgentMessaging messaging,
    ISiteRepository siteRepo,
    IAgentRepository agentRepo
) : IRequestHandler<SiteRestartCommand, Result<SiteFanoutResponseDto>>
{
    private const int MaxMessageLength = 512;

    public async Task<Result<SiteFanoutResponseDto>> Handle(SiteRestartCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var site = await siteRepo.GetByIdAsync(cmd.SiteId);
        if (site is null)
            return Result<SiteFanoutResponseDto>.Failure(Error.NotFound("Site not found."));

        if (cmd.Message?.Length > MaxMessageLength)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("Message", $"message must be at most {MaxMessageLength} characters."));

        var allAgents = (await agentRepo.GetBySiteIdAsync(cmd.SiteId)).ToList();
        var onlineAgents = allAgents.Where(a => a.EffectiveStatus == AgentStatus.Online).ToList();
        if (onlineAgents.Count == 0)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("SiteId", "No online agents available in this site."));

        var delay = Math.Clamp(cmd.DelaySeconds, 1, 3600);
        var payload = JsonSerializer.Serialize(new { delaySeconds = delay, force = cmd.Force, message = cmd.Message });

        var (dispatchId, envelope) = BuildEnvelope(
            CommandType.Restart,
            payload,
            site.ClientId,
            cmd.SiteId);
        await messaging.PublishSiteFanoutCommandAsync(site.ClientId, cmd.SiteId, envelope, ct);

        return Result<SiteFanoutResponseDto>.Success(new SiteFanoutResponseDto(
            dispatchId,
            NatsSubjectBuilder.SiteAgentsCommandSubject(site.ClientId, cmd.SiteId),
            "site",
            envelope.IdempotencyKey,
            onlineAgents.Count));
    }

    internal static (Guid dispatchId, CommandDispatchEnvelope envelope) BuildEnvelope(
        CommandType type, string payload, Guid clientId, Guid siteId)
    {
        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        return (dispatchId, new CommandDispatchEnvelope
        {
            DispatchId = dispatchId,
            CommandType = CommandTypeWireMapper.ToWireValue(type),
            TargetScope = "site",
            TargetClientId = clientId,
            TargetSiteId = siteId,
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = null,
            IdempotencyKey = $"fanout:{dispatchId:N}",
            Payload = payload
        });
    }
}

/// <summary>
/// Envia comando de desligamento em massa para todos os agentes online do site,
/// via fan-out no subject tenant.{c}.site.{s}.agents.command.
/// </summary>
public sealed class SiteShutdownCommandHandler(
    IAgentMessaging messaging,
    ISiteRepository siteRepo,
    IAgentRepository agentRepo
) : IRequestHandler<SiteShutdownCommand, Result<SiteFanoutResponseDto>>
{
    private const int MaxMessageLength = 512;

    public async Task<Result<SiteFanoutResponseDto>> Handle(SiteShutdownCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var site = await siteRepo.GetByIdAsync(cmd.SiteId);
        if (site is null)
            return Result<SiteFanoutResponseDto>.Failure(Error.NotFound("Site not found."));

        if (cmd.Message?.Length > MaxMessageLength)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("Message", $"message must be at most {MaxMessageLength} characters."));

        var allAgents = (await agentRepo.GetBySiteIdAsync(cmd.SiteId)).ToList();
        var onlineAgents = allAgents.Where(a => a.EffectiveStatus == AgentStatus.Online).ToList();
        if (onlineAgents.Count == 0)
            return Result<SiteFanoutResponseDto>.Failure(Error.Validation("SiteId", "No online agents available in this site."));

        var delay = Math.Clamp(cmd.DelaySeconds, 1, 3600);
        var payload = JsonSerializer.Serialize(new { delaySeconds = delay, force = cmd.Force, message = cmd.Message });

        var (dispatchId, envelope) = SiteRestartCommandHandler.BuildEnvelope(
            CommandType.Shutdown,
            payload,
            site.ClientId,
            cmd.SiteId);
        await messaging.PublishSiteFanoutCommandAsync(site.ClientId, cmd.SiteId, envelope, ct);

        return Result<SiteFanoutResponseDto>.Success(new SiteFanoutResponseDto(
            dispatchId,
            NatsSubjectBuilder.SiteAgentsCommandSubject(site.ClientId, cmd.SiteId),
            "site",
            envelope.IdempotencyKey,
            onlineAgents.Count));
    }
}

/// <summary>
/// Envia Wake-on-LAN em massa para todos os agentes OFFLINE do site que possuam MAC
/// (Agent.MacAddress ou adaptadores de rede). Para cada MAC, publica um fan-out no site
/// para que agentes online retransmitam o magic packet (relay).
/// </summary>
public sealed class SiteWakeOnLanCommandHandler(
    IAgentMessaging messaging,
    ISiteRepository siteRepo,
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<SiteWakeOnLanCommand, Result<SiteWakeOnLanResponseDto>>
{
    public async Task<Result<SiteWakeOnLanResponseDto>> Handle(SiteWakeOnLanCommand cmd, CancellationToken ct)
    {
        if (!messaging.IsConnected)
            return Result<SiteWakeOnLanResponseDto>.Failure(Error.Validation("NATS", "NATS realtime transport unavailable."));

        var site = await siteRepo.GetByIdAsync(cmd.SiteId);
        if (site is null)
            return Result<SiteWakeOnLanResponseDto>.Failure(Error.NotFound("Site not found."));

        var allAgents = (await agentRepo.GetBySiteIdAsync(cmd.SiteId)).ToList();
        var onlineAgents = allAgents.Where(a => a.EffectiveStatus == AgentStatus.Online).ToList();
        var offlineAgents = allAgents
            .Where(a => a.EffectiveStatus != AgentStatus.Online)
            .ToList();

        // Sem relays online, o magic packet nunca chegaria à rede local — aborta.
        if (onlineAgents.Count == 0)
            return Result<SiteWakeOnLanResponseDto>.Failure(Error.Validation("SiteId", "No online agents available in this site to relay the Wake-on-LAN packet."));

        var targets = new List<(string agentName, List<string> macs)>();
        foreach (var agent in offlineAgents)
        {
            var macs = await CollectMacAddressesAsync(agent.Id, agent.MacAddress, ct);
            if (macs.Count > 0)
                targets.Add((agent.DisplayName ?? agent.Hostname, macs));
        }

        if (targets.Count == 0)
            return Result<SiteWakeOnLanResponseDto>.Failure(Error.Validation("SiteId", "No offline agents with a registered MAC address."));

        var dispatchId = IdGenerator.NewId();
        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddSeconds(60);

        var allMacs = new List<string>();
        foreach (var (_, macs) in targets)
        {
            foreach (var mac in macs)
            {
                // Dedup por MAC entre os alvos.
                if (allMacs.Contains(mac)) continue;
                allMacs.Add(mac);

                var wolPayload = JsonSerializer.Serialize(new { macAddress = mac, broadcastAddress = "255.255.255.255" });
                var envelope = new CommandDispatchEnvelope
                {
                    DispatchId = dispatchId,
                    CommandType = CommandTypeWireMapper.ToWireValue(CommandType.WakeOnLan),
                    TargetScope = "site",
                    TargetClientId = site.ClientId,
                    TargetSiteId = cmd.SiteId,
                    IssuedAtUtc = issuedAtUtc,
                    ExpiresAtUtc = expiresAtUtc,
                    IdempotencyKey = $"wol-site-{cmd.SiteId}-{dispatchId:N}-{mac}",
                    Payload = wolPayload
                };
                await messaging.PublishSiteFanoutCommandAsync(site.ClientId, cmd.SiteId, envelope, ct);
            }
        }

        return Result<SiteWakeOnLanResponseDto>.Success(new SiteWakeOnLanResponseDto(
            dispatchId,
            targets.Count,
            onlineAgents.Count,
            targets.Select(t => t.agentName).ToList(),
            allMacs,
            expiresAtUtc));
    }

    private static readonly Regex MacAddressRegex = new(
        "^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private async Task<List<string>> CollectMacAddressesAsync(
        Guid agentId, string? primaryMac, CancellationToken ct)
    {
        var macs = new List<string>();
        AddValidMac(primaryMac, macs);

        try
        {
            var components = await hardwareRepo.GetComponentsAsync(agentId);
            foreach (var adapter in components.NetworkAdapters)
            {
                AddValidMac(adapter.MacAddress, macs);
            }
        }
        catch
        {
            // Ignora falha ao ler hardware — usa apenas o MAC primário.
        }

        return macs;
    }

    private static void AddValidMac(string? mac, List<string> macs)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var trimmed = mac.Trim();
        if (!MacAddressRegex.IsMatch(trimmed)) return;
        if (macs.Contains(trimmed)) return;
        macs.Add(trimmed);
    }
}