using System.Collections.Concurrent;
using System.Security.Cryptography;
using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Discovery.Api.Hubs;

public class AgentHub : Hub
{
    private static readonly ConcurrentDictionary<string, Guid> ConnectedAgents = new();
    private static readonly ConcurrentDictionary<Guid, HeartbeatState> LastPersistedHeartbeat = new();
    private static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(15);

    // Nonce de desafio por connectionId para o handshake secundário.
    private static readonly ConcurrentDictionary<string, string> PendingChallenges = new();

    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentMessaging _messaging;
    private readonly IPermissionService _permissionService;
    private readonly IConfiguration _configuration;
    private readonly IAgentTlsCertificateProbe _tlsCertProbe;
    private readonly IRemoteDebugLogRelay _remoteDebugLogRelay;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IClientRepository clientRepo,
        ICommandRepository commandRepo,
        IAgentMessaging messaging,
        IPermissionService permissionService,
        IConfiguration configuration,
        IAgentTlsCertificateProbe tlsCertProbe,
        IRemoteDebugLogRelay remoteDebugLogRelay,
        ILogger<AgentHub> logger)
    {
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _clientRepo = clientRepo;
        _commandRepo = commandRepo;
        _messaging = messaging;
        _permissionService = permissionService;
        _configuration = configuration;
        _tlsCertProbe = tlsCertProbe;
        _remoteDebugLogRelay = remoteDebugLogRelay;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var isAgent = Context.Items.ContainsKey("AgentId");
        var isUser = Context.Items["UserId"] is Guid;

        if (!isAgent && !isUser)
        {
            // Conexao sem autenticacao: abortar imediatamente
            Context.Abort();
            return;
        }

        // Handshake secundário anti-MITM: quando habilitado, o agent deve chamar
        // SecureHandshakeAsync antes de qualquer outro método.
        if (isAgent)
        {
            var handshakeEnabled = _configuration.GetValue<bool>("Security:AgentConnection:HandshakeEnabled");
            if (handshakeEnabled)
            {
                // Gera nonce de desafio e envia ao agent junto com o hash TLS esperado.
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                PendingChallenges[Context.ConnectionId] = nonce;
                Context.Items["HandshakeState"] = "PreAuth";

                var expectedTlsHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync();
                await Clients.Caller.SendAsync("HandshakeChallenge", nonce, expectedTlsHash ?? string.Empty);
            }
            else
            {
                // Flag desligada: considera direto como autenticado.
                Context.Items["HandshakeState"] = "Authenticated";
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        PendingChallenges.TryRemove(Context.ConnectionId, out _);

        if (ConnectedAgents.TryRemove(Context.ConnectionId, out var agentId))
        {
            var agent = await _agentRepo.GetByIdAsync(agentId);
            await _agentRepo.UpdateStatusAsync(agentId, AgentStatus.Offline, null);
            LastPersistedHeartbeat.TryRemove(agentId, out _);
            await PublishAgentStatusChangedAsync(agent ?? new() { Id = agentId }, AgentStatus.Offline);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Handshake secundário anti-MITM (estilo MeshCentral).
    /// O agent informa o fingerprint TLS que ele observou na conexão com o edge.
    /// O servidor compara com o hash esperado (obtido via probe na URL configurada).
    /// Se divergir, a conexão é encerrada — indica MITM ativo.
    /// Controlado por Security:AgentConnection:HandshakeEnabled.
    /// </summary>
    public async Task SecureHandshakeAsync(string agentObservedTlsHash)
    {
        if (Context.Items["AgentId"] is not Guid authenticatedId)
            throw new HubException("Not authenticated as agent.");

        var handshakeEnabled = _configuration.GetValue<bool>("Security:AgentConnection:HandshakeEnabled");
        if (!handshakeEnabled)
        {
            // Flag desligada: ack imediato sem validação.
            Context.Items["HandshakeState"] = "Authenticated";
            await Clients.Caller.SendAsync("HandshakeAck", true, "Handshake disabled by server configuration.");
            return;
        }

        // Verifica que existe um nonce pendente para esta conexão.
        if (!PendingChallenges.TryGetValue(Context.ConnectionId, out _))
        {
            _logger.LogWarning("Agent {AgentId} called SecureHandshakeAsync but no pending challenge found.", authenticatedId);
            throw new HubException("No pending challenge. Reconnect and try again.");
        }

        // Valida o fingerprint TLS reportado pelo agent contra o hash que o servidor esperava.
        var expectedTlsHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync();

        if (string.IsNullOrWhiteSpace(expectedTlsHash))
        {
            // Não foi configurada uma URL de probe; aceita sem validação de hash e loga aviso.
            _logger.LogWarning(
                "Agent {AgentId} handshake: TlsCertificateProbeUrl not configured. " +
                "Skipping TLS hash comparison (Security:AgentConnection:TlsCertificateProbeUrl is empty).",
                authenticatedId);
        }
        else if (!string.Equals(expectedTlsHash, agentObservedTlsHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "MITM DETECTED: Agent {AgentId} reported TLS hash {ObservedHash} but server expected {ExpectedHash}. " +
                "Aborting connection.",
                authenticatedId,
                agentObservedTlsHash,
                expectedTlsHash);

            await Clients.Caller.SendAsync("HandshakeAck", false, "TLS certificate mismatch. Connection aborted.");
            Context.Abort();
            return;
        }

        // Handshake concluído com sucesso.
        PendingChallenges.TryRemove(Context.ConnectionId, out _);
        Context.Items["HandshakeState"] = "Authenticated";
        Context.Items["ConfirmedTlsFingerprint"] = agentObservedTlsHash?.Trim() ?? string.Empty;

        _logger.LogInformation(
            "Agent {AgentId} handshake completed. TLS fingerprint confirmed: {Hash}",
            authenticatedId,
            agentObservedTlsHash);

        await Clients.Caller.SendAsync("HandshakeAck", true, "Handshake completed successfully.");
    }

    // Garante que o handshake foi concluído antes de aceitar métodos críticos.
    // Só bloqueia quando a flag HandshakeEnabled está ativa, mantendo compatibilidade.
    private void RequireHandshake()
    {
        var handshakeEnabled = _configuration.GetValue<bool>("Security:AgentConnection:HandshakeEnabled");
        if (!handshakeEnabled) return;

        var state = Context.Items["HandshakeState"] as string;
        if (state != "Authenticated")
            throw new HubException("Secure handshake required before calling this method.");
    }

    /// <summary>
    /// Agent se registra ao conectar, informando seu ID.
    /// O agentId do parametro e ignorado — a identidade vem do token autenticado.
    /// </summary>
    public async Task RegisterAgent(Guid agentId, string? ipAddress)
    {
        if (Context.Items["AgentId"] is not Guid authenticatedId)
            throw new HubException("Not authenticated as agent.");

        RequireHandshake();

        // Usa o ID autenticado pelo middleware, ignora o parametro (nao confiavel)
        await EnsureConnectionMappedAsync(authenticatedId);
        await _agentRepo.UpdateStatusAsync(authenticatedId, AgentStatus.Online, ipAddress);
        LastPersistedHeartbeat[authenticatedId] = new HeartbeatState(DateTime.UtcNow, ipAddress);
        var agent = await _agentRepo.GetByIdAsync(authenticatedId) ?? new Agent { Id = authenticatedId };
        await PublishAgentStatusChangedAsync(agent, AgentStatus.Online);

        // Envia comandos pendentes
        var pendingCommands = await _commandRepo.GetPendingByAgentIdAsync(authenticatedId);
        foreach (var cmd in pendingCommands)
        {
            await Clients.Caller.SendAsync("ExecuteCommand", cmd.Id, cmd.CommandType, cmd.Payload);
            await _commandRepo.UpdateStatusAsync(cmd.Id, CommandStatus.Sent, null, null, null);
        }
    }

    /// <summary>
    /// Agent informa resultado de um comando executado.
    /// So o agent dono do comando pode reportar resultado.
    /// </summary>
    public async Task CommandResult(Guid commandId, int exitCode, string? output, string? errorMessage)
    {
        if (Context.Items["AgentId"] is not Guid authenticatedId)
            throw new HubException("Not authenticated as agent.");

        RequireHandshake();

        // Verifica que o comando pertence ao agent autenticado
        var command = await _commandRepo.GetByIdAsync(commandId);
        if (command is null || command.AgentId != authenticatedId)
            throw new HubException("Command not found or not authorized.");

        var status = exitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
        await _commandRepo.UpdateStatusAsync(commandId, status, output, exitCode, errorMessage);

        Guid? clientId = null;
        Guid? siteId = null;

        var agent = await _agentRepo.GetByIdAsync(authenticatedId);
        siteId = agent?.SiteId;
        if (siteId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                "CommandCompleted",
                new { commandId, exitCode, output, errorMessage },
                clientId,
                siteId));
    }

    public async Task PushRemoteDebugLog(
        Guid sessionId,
        string? level,
        string? message,
        DateTime? timestampUtc = null,
        long? sequence = null)
    {
        if (Context.Items["AgentId"] is not Guid authenticatedId)
            throw new HubException("Not authenticated as agent.");

        RequireHandshake();
        await EnsureConnectionMappedAsync(authenticatedId);

        await _remoteDebugLogRelay.RelayAsync(
            new RemoteDebugInboundLogEntry(
                sessionId,
                authenticatedId,
                message,
                level,
                timestampUtc,
                sequence),
            RemoteDebugTransportNames.SignalR,
            Context.ConnectionAborted);
    }

    /// <summary>
    /// Agent envia heartbeat periodico.
    /// O agentId do parametro e ignorado — a identidade vem do token autenticado.
    /// </summary>
    public async Task Heartbeat(Guid agentId, string? ipAddress)
    {
        if (Context.Items["AgentId"] is not Guid authenticatedId)
            throw new HubException("Not authenticated as agent.");

        RequireHandshake();

        await EnsureConnectionMappedAsync(authenticatedId);

        var now = DateTime.UtcNow;

        if (LastPersistedHeartbeat.TryGetValue(authenticatedId, out var state)
            && now - state.LastPersistedAt < HeartbeatWriteInterval
            && string.Equals(state.LastIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _agentRepo.UpdateStatusAsync(authenticatedId, AgentStatus.Online, ipAddress);
            LastPersistedHeartbeat[authenticatedId] = new HeartbeatState(now, ipAddress);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Heartbeat update timed out for agent {AgentId}.", authenticatedId);
        }
    }

    /// <summary>
    /// Dashboard se inscreve para receber atualizacoes globais em tempo real.
    /// Requer permissao Dashboard.View em escopo Global.
    /// </summary>
    public async Task JoinDashboard()
    {
        if (Context.Items["UserId"] is not Guid userId)
            throw new HubException("Not authenticated as user.");

        var hasAccess = await _permissionService.HasPermissionAsync(
            userId, ResourceType.Dashboard, ActionType.View, ScopeLevel.Global);
        if (!hasAccess)
            throw new HubException("Access denied to global dashboard.");

        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.Global);
    }

    public async Task JoinClientDashboard(Guid clientId)
    {
        if (Context.Items["UserId"] is not Guid userId)
            throw new HubException("Not authenticated as user.");

        var hasAccess = await _permissionService.HasPermissionAsync(
            userId, ResourceType.Dashboard, ActionType.View, ScopeLevel.Client, clientId);
        if (!hasAccess)
            throw new HubException("Access denied to this client dashboard.");

        var client = await _clientRepo.GetByIdAsync(clientId);
        if (client is null)
            throw new HubException("Client not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.ForClient(clientId));
    }

    public async Task JoinSiteDashboard(Guid clientId, Guid siteId)
    {
        if (Context.Items["UserId"] is not Guid userId)
            throw new HubException("Not authenticated as user.");

        var site = await _siteRepo.GetByIdAsync(siteId);
        if (site is null || site.ClientId != clientId)
            throw new HubException("Site not found for this client.");

        var hasAccess = await _permissionService.HasPermissionAsync(
            userId, ResourceType.Dashboard, ActionType.View, ScopeLevel.Site, siteId, clientId);
        if (!hasAccess)
            throw new HubException("Access denied to this site dashboard.");

        await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroupNames.ForSite(siteId));
    }

    /// <summary>
    /// Verifica se um agent específico está conectado.
    /// </summary>
    public static bool IsAgentConnected(Guid agentId)
    {
        return ConnectedAgents.Values.Contains(agentId);
    }

    public static int ConnectedAgentCount => ConnectedAgents.Count;

    /// <summary>
    /// Retorna a connection ID de um agent conectado.
    /// </summary>
    public static string? GetConnectionId(Guid agentId)
    {
        return ConnectedAgents.FirstOrDefault(x => x.Value == agentId).Key;
    }

    private async Task EnsureConnectionMappedAsync(Guid agentId)
    {
        if (ConnectedAgents.TryGetValue(Context.ConnectionId, out var existingAgentId))
        {
            if (existingAgentId == agentId)
                return;

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"agent-{existingAgentId}");
        }

        ConnectedAgents[Context.ConnectionId] = agentId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-{agentId}");
    }

    private async Task PublishAgentStatusChangedAsync(Agent agent, AgentStatus status)
    {
        Guid? clientId = null;
        Guid? siteId = agent.SiteId == Guid.Empty ? null : agent.SiteId;

        if (siteId.HasValue)
        {
            var site = await _siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                "AgentStatusChanged",
                new { agentId = agent.Id, status = status.ToString() },
                clientId,
                siteId));
    }

    private readonly record struct HeartbeatState(DateTime LastPersistedAt, string? LastIpAddress);
}
