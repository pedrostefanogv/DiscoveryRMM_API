using System.Text.Json;
using Discovery.Api.Cqrs.RemoteSessions.CommandHandlers;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Testes do <see cref="ChangeRemoteSessionBandwidthCommandHandler"/>.
///
/// Cobrem (a) validacao que ja existia, (b) o endurecimento contra a politica
/// do servidor (ServerMaxKbps / range do modo auto), (c) o contrato de wire do
/// payload publicado no dispatcher e (d) os caminhos de sessao nao encontrada /
/// sessao nao-Screen.
/// </summary>
public class RemoteSessionBandwidthCommandHandlerTests
{
    // ── Validacao ──

    [Test]
    public async Task Handle_ModoInvalido_Falha()
    {
        var (handler, dispatcher) = CreateHandler();

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "turbo"), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Field, Is.EqualTo("mode"));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task Handle_CeilingNegativo_Falha()
    {
        var (handler, dispatcher) = CreateHandler();

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "manual", CeilingKbps: -1), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task Handle_AutoMaxMenorQueAutoMin_Falha()
    {
        var (handler, dispatcher) = CreateHandler();

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId,
            Mode: "auto", AutoMinKbps: 8000, AutoMaxKbps: 2000), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Field, Is.EqualTo("autoMaxKbps"));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    // ── Clamp contra a politica do servidor (item 2d/2e/2f) ──

    [Test]
    public async Task Handle_ManualPresetAcimaDoServerMax_Clampa()
    {
        // UI manda o preset "max" (20 Mbps) e o servidor tem teto de 5 Mbps:
        // a decisao e CLAMPAR (nao rejeitar) para a UI nao quebrar por config.
        var (handler, dispatcher) = CreateHandler(serverMaxKbps: 5000);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "manual", CeilingKbps: 20000), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Commands, Has.Count.EqualTo(1));
        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(5000));
        Assert.That(doc.RootElement.GetProperty("serverMaxKbps").GetInt32(), Is.EqualTo(5000));
    }

    [Test]
    public async Task Handle_ClienteTentaAfrouxarServerMax_EscaladaEhIgnorada()
    {
        // autoMax=999999 e serverMaxKbps=0 vindos do cliente NAO podem
        // sobrescrever a politica do servidor (5 Mbps).
        var (handler, dispatcher) = CreateHandler(serverMaxKbps: 5000);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId,
            Mode: "auto", AutoMaxKbps: 999999, ServerMaxKbps: 0), default);

        Assert.That(result.IsSuccess, Is.True);
        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("autoMaxKbps").GetInt32(), Is.EqualTo(5000));
        Assert.That(doc.RootElement.GetProperty("serverMaxKbps").GetInt32(), Is.EqualTo(5000));
    }

    [Test]
    public async Task Handle_ServerMaxZero_NaoRestringe()
    {
        // ServerMaxKbps=0 = "sem restricao do servidor": o valor do cliente passa.
        var (handler, dispatcher) = CreateHandler(serverMaxKbps: 0);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "manual", CeilingKbps: 20000), default);

        Assert.That(result.IsSuccess, Is.True);
        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(20000));
        Assert.That(doc.RootElement.GetProperty("serverMaxKbps").GetInt32(), Is.EqualTo(0));
    }

    // ── Contrato de wire: nulos OMITIDOS ──

    [Test]
    public async Task Handle_ModoHd_NaoPublicaCamposNulos()
    {
        // O cliente so troca o MODO. O payload nao pode conter
        // `"autoMinKbps":null` — o contrato "ausente = preserva" tem de ser
        // literal no wire, nao dependente de como o Go interpreta null.
        var (handler, dispatcher) = CreateHandler();

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "hd"), default);

        Assert.That(result.IsSuccess, Is.True);
        var payload = dispatcher.Commands[0].Payload;

        Assert.That(payload, Does.Not.Contain("autoMinKbps"), $"payload inesperado: {payload}");
        Assert.That(payload, Does.Not.Contain("autoMaxKbps"), $"payload inesperado: {payload}");
        Assert.That(payload, Does.Not.Contain("autoStepKbps"), $"payload inesperado: {payload}");
        Assert.That(payload, Does.Not.Contain(":null"), $"payload inesperado: {payload}");

        // action/sessionId/mode SEMPRE presentes (o Go roteia por action).
        using var doc = JsonDocument.Parse(payload);
        Assert.That(doc.RootElement.GetProperty("action").GetString(), Is.EqualTo("bandwidth"));
        Assert.That(doc.RootElement.GetProperty("sessionId").GetGuid(), Is.EqualTo(SessionId));
        Assert.That(doc.RootElement.GetProperty("mode").GetString(), Is.EqualTo("hd"));
    }

    [Test]
    public async Task Handle_ModoManualSemCeiling_UsaManualDefaultKbpsDoServidor()
    {
        // "manual sem preset" = preset DEFAULT do servidor (ManualDefaultKbps),
        // nao "sem limite": senao ManualDefaultKbps vira config morta e a doc
        // do RemoteAccessBandwidthOptions ("teto padrao do modo manual quando
        // nenhum preset e escolhido") deixa de valer.
        var (handler, dispatcher) = CreateHandler(manualDefaultKbps: 6000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "manual"), default);

        var payload = dispatcher.Commands[0].Payload;
        Assert.That(payload, Does.Not.Contain(":null"), $"payload inesperado: {payload}");
        using var doc = JsonDocument.Parse(payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(6000));
        Assert.That(doc.RootElement.TryGetProperty("autoMinKbps", out _), Is.False);
    }

    [Test]
    public async Task Handle_ModoManualSemCeiling_RespeitaServerMax()
    {
        // ManualDefaultKbps=8000 com ServerMaxKbps=4000: o default do servidor
        // nao pode furar o proprio teto absoluto do servidor.
        var (handler, dispatcher) = CreateHandler(serverMaxKbps: 4000, manualDefaultKbps: 8000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "manual"), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(4000));
    }

    // ── REGRESSAO: modo auto precisa partir de AutoStartKbps, nao do piso ──

    [Test]
    public async Task Handle_AutoSemCeiling_EnviaAutoStartKbps_NaoPiso()
    {
        // REGRESSAO (toggle HD -> Auto do incidente): com ceilingKbps OMITIDO o
        // Go faz -1 -> ConfigureBandwidth converte para 0 -> SetMode(auto, 0)
        // com teto corrente <= 0 cai no PISO AutoMin (1000 kbps), nao no
        // AutoStartKbps (8000). O gate entao pausa a captura (~1 Mbps) numa
        // sessao que estava em HD sem teto.
        var (handler, dispatcher) = CreateHandler(
            autoMinKbps: 1000, autoMaxKbps: 30000, autoStartKbps: 8000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "auto"), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        var ceiling = doc.RootElement.GetProperty("ceilingKbps").GetInt32();
        Assert.That(ceiling, Is.EqualTo(8000), "auto deve partir de AutoStartKbps");
        Assert.That(ceiling, Is.Not.EqualTo(1000), "auto NAO pode cair no piso AutoMin");
    }

    [Test]
    public async Task Handle_AutoSemCeiling_StartClampadoNoRangeAuto()
    {
        // AutoStartKbps fora de [AutoMin,AutoMax] (config incoerente) e clampeado.
        var (handler, dispatcher) = CreateHandler(
            autoMinKbps: 2000, autoMaxKbps: 5000, autoStartKbps: 9000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "auto"), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(5000));
    }

    [Test]
    public async Task Handle_AutoSemCeiling_StartClampadoNoServerMax()
    {
        var (handler, dispatcher) = CreateHandler(
            autoMinKbps: 1000, autoMaxKbps: 30000, autoStartKbps: 8000, serverMaxKbps: 3000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "auto"), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(3000));
    }

    [Test]
    public async Task Handle_AutoComCeilingDoCliente_UsaValorDoCliente()
    {
        // Se o cliente manda o ponto de partida, ele vence o AutoStartKbps.
        var (handler, dispatcher) = CreateHandler(autoStartKbps: 8000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId,
            Mode: "auto", CeilingKbps: 12000), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(12000));
    }

    [Test]
    public async Task Handle_AutoRangeInvertidoNaConfig_NaoLanca()
    {
        // Config com AutoMin > AutoMax nao pode derrubar o clamp (ArgumentException).
        var (handler, dispatcher) = CreateHandler(
            autoMinKbps: 9000, autoMaxKbps: 3000, autoStartKbps: 8000);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "auto"), default);

        Assert.That(result.IsSuccess, Is.True);
        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(9000));
    }

    // ── HD: teto PRECISA ir a zero ──

    [Test]
    public async Task Handle_ModoHd_SempreEnviaCeilingZero()
    {
        // Em HD o teto precisa ir a ZERO explicitamente. Se fosse omitido, o
        // gate preservaria o teto do modo anterior e voltar de manual para HD
        // continuaria freando a captura.
        var (handler, dispatcher) = CreateHandler();

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId,
            Mode: "hd", CeilingKbps: 1234), default);

        var payload = dispatcher.Commands[0].Payload;
        using var doc = JsonDocument.Parse(payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(0),
            "HD deve zerar o teto, ignorando um ceiling do cliente");
        Assert.That(doc.RootElement.GetProperty("mode").GetString(), Is.EqualTo("hd"));
    }

    [Test]
    public async Task Handle_ModoAuto_EnviaRangeEDefaultsDoServidorQuandoClienteNaoManda()
    {
        // Em auto o range E o ponto de partida vem SEMPRE preenchidos (do
        // servidor quando o cliente nao manda), em vez de depender do
        // "preserva o corrente" e do piso do gate.
        var (handler, dispatcher) = CreateHandler(
            autoMinKbps: 1500, autoMaxKbps: 12000, autoStepKbps: 2500,
            serverMaxKbps: 0, autoStartKbps: 9000);

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "auto"), default);

        using var doc = JsonDocument.Parse(dispatcher.Commands[0].Payload);
        Assert.That(doc.RootElement.GetProperty("ceilingKbps").GetInt32(), Is.EqualTo(9000));
        Assert.That(doc.RootElement.GetProperty("autoMinKbps").GetInt32(), Is.EqualTo(1500));
        Assert.That(doc.RootElement.GetProperty("autoMaxKbps").GetInt32(), Is.EqualTo(12000));
        Assert.That(doc.RootElement.GetProperty("autoStepKbps").GetInt32(), Is.EqualTo(2500));
    }

    [Test]
    public async Task Handle_PayloadComNulosOmitidos_PassaNoSpecialCommandPayloadValidator()
    {
        // Regressao do item (1) + correcao do lead: omitir os campos nulos NAO
        // pode quebrar o dispatcher. O AgentCommandDispatcher SEMPRE chama
        // SpecialCommandPayloadValidator.TryNormalize(CommandType.RemoteSessionStart,
        // payload) e LANCA se falhar. O validador so checa campo PRESENTE, entao
        // a omissao e segura — este teste prova isso com o validador REAL, e nao
        // com um fake.
        var (handler, dispatcher) = CreateHandler();
        var validator = new SpecialCommandPayloadValidator();

        foreach (var mode in new[] { "hd", "auto", "manual" })
        {
            await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
                AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: mode), default);
        }

        Assert.That(dispatcher.Commands, Has.Count.EqualTo(3));
        foreach (var command in dispatcher.Commands)
        {
            var ok = validator.TryNormalize(command.CommandType, command.Payload, out var normalized, out var error);
            Assert.That(ok, Is.True, $"validador rejeitou o payload: {error} | {command.Payload}");
            Assert.That(error, Is.Empty);
            Assert.That(normalized, Does.Not.Contain(":null"));
        }
    }

    [Test]
    public async Task Handle_UsaCommandTypeRemoteSessionStart_ERoteiaPorAction()
    {
        // O agent roteia o comando de runtime pelo campo `action` do payload
        // (manager.go: HandleCommand), nao pelo CommandType. Documenta o contrato.
        var (handler, dispatcher) = CreateHandler();

        await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "hd"), default);

        Assert.That(dispatcher.Commands[0].CommandType, Is.EqualTo(CommandType.RemoteSessionStart));
    }

    // ── Assimetria de contrato START x RUNTIME (achado do item 4, NAO corrigido) ──

    [Test]
    public void Validator_ValidaBandwidthAninhadoDoStart_MasIgnoraCamposNoTopoDoRuntime()
    {
        // ACHADO (reportar, nao corrigir): o START valida o bloco ANINHADO
        // "bandwidth" { mode, ceilingKbps, ... }. O RUNTIME publicado pelo
        // handler usa os mesmos nomes NO TOPO do payload (action/mode/ceilingKbps
        // na raiz). Logo, o runtime NAO passa por essa checagem especifica: ele
        // so nao falha porque o validador e permissivo com campos desconhecidos.
        //
        // Este teste DOCUMENTA a assimetria. Se um dia o bloco aninhado for
        // estendido (ex.: autoRaiseFactor validado), o runtime continuara fora.
        var validator = new SpecialCommandPayloadValidator();

        // START: modo invalido DENTRO do bloco aninhado -> rejeitado.
        var startPayload = """{"action":"start","sessionId":"s","bandwidth":{"mode":"turbo"}}""";
        var startOk = validator.TryNormalize(CommandType.RemoteSessionStart, startPayload, out _, out var startErr);
        Assert.That(startOk, Is.False);
        Assert.That(startErr, Does.Contain("bandwidth.mode"));

        // RUNTIME: o MESMO modo invalido, porem no TOPO -> passa no validador.
        // Quem barra e o handler (validacao de negocio), nao o validador.
        var runtimePayload = """{"action":"bandwidth","sessionId":"s","mode":"turbo","ceilingKbps":-5}""";
        var runtimeOk = validator.TryNormalize(CommandType.RemoteSessionStart, runtimePayload, out _, out var runtimeErr);
        Assert.That(runtimeOk, Is.True,
            $"o validador passou a checar o topo; revisar o achado. erro={runtimeErr}");
    }

    // ── Sessao ──

    [Test]
    public async Task Handle_SessaoNaoEncontrada_NotFound()
    {
        var (handler, dispatcher) = CreateHandler(useNullSession: true);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "hd"), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task Handle_SessaoNaoScreen_SucessoSemDispatch()
    {
        // Terminal/arquivos: aceitamos mas nao publicamos. Recusar viraria
        // erro espurio quando o viewer envia antes de a tela subir.
        var (handler, dispatcher) = CreateHandler(kind: RemoteSessionKind.Terminal);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "hd"), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task Handle_SessaoKindAll_Publica()
    {
        var (handler, dispatcher) = CreateHandler(kind: RemoteSessionKind.All);

        var result = await handler.Handle(new ChangeRemoteSessionBandwidthCommand(
            AgentId: Guid.NewGuid(), SessionId: SessionId, UserId: UserId, Mode: "hd"), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Commands, Has.Count.EqualTo(1));
    }

    // ── Infra dos testes ──

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Monta o handler com um dispatcher que captura o payload publicado e uma
    /// sessao ativa fabricada (ou nula, para o caminho NotFound).
    /// </summary>
    private static (ChangeRemoteSessionBandwidthCommandHandler Handler, CapturingDispatcher Dispatcher)
        CreateHandler(
            RemoteSessionKind kind = RemoteSessionKind.Screen,
            bool useNullSession = false,
            int autoMinKbps = 1000,
            int autoMaxKbps = 30000,
            int autoStepKbps = 2000,
            int serverMaxKbps = 0,
            int autoStartKbps = 8000,
            int manualDefaultKbps = 8000)
    {
        var dispatcher = new CapturingDispatcher();
        var options = Options.Create(new RemoteAccessOptions
        {
            Quality = new RemoteAccessQualityOptions
            {
                Bandwidth = new RemoteAccessBandwidthOptions
                {
                    AutoMinKbps = autoMinKbps,
                    AutoMaxKbps = autoMaxKbps,
                    AutoStepKbps = autoStepKbps,
                    ServerMaxKbps = serverMaxKbps,
                    AutoStartKbps = autoStartKbps,
                    ManualDefaultKbps = manualDefaultKbps,
                }
            }
        });

        RemoteSession? session = useNullSession
            ? null
            : new RemoteSession
            {
                Id = SessionId,
                AgentId = Guid.NewGuid(),
                UserId = UserId,
                Kind = kind,
                Status = "active",
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            };

        var handler = new ChangeRemoteSessionBandwidthCommandHandler(
            new FakeSessionManager(session),
            dispatcher,
            options,
            NullLogger<ChangeRemoteSessionBandwidthCommandHandler>.Instance);

        return (handler, dispatcher);
    }

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Commands { get; } = new();

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class FakeSessionManager(RemoteSession? session) : IRemoteSessionManager
    {
        public Task<RemoteSession?> GetActiveForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
            => Task.FromResult(session);

        public Task<RemoteSession?> GetForUserAsync(Guid sessionId, Guid userId, bool allowPending, CancellationToken ct = default)
            => Task.FromResult(session);

        public Task<RemoteSession?> GetRawSessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(session);

        public Task<RemoteSession> CreateSessionAsync(
            Guid agentId, Guid userId, Guid tenantId, Guid siteId,
            RemoteSessionKind kind, RemoteTransport transport, QualityProfile quality, RemoteCodec codec,
            string natsSubject, bool force = false, CancellationToken ct = default,
            bool consentRequired = false, DateTime? consentDeadlineAt = null,
            string? consentPolicySource = null, Guid? id = null)
            => throw new NotSupportedException();

        public Task<RemoteSession> RenewSessionAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RemoteSession> CloseSessionAsync(Guid sessionId, string reason, Guid? actorUserId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RemoteSession> ApplyConsentDecisionAsync(
            Guid sessionId, bool accepted, string outcome, string? reason, string? promptBackend,
            CancellationToken ct = default, bool auto = false)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteSession>> GetActiveSessionsForAgentAsync(Guid agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RemoteSession> SetNatsSubjectAsync(Guid sessionId, string natsSubject, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RemoteSession> UpdateQualityAsync(Guid sessionId, QualityProfile quality, RemoteCodec? codec = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AuditAsync(Guid sessionId, string eventType, string? details = null, string? actorUserId = null, string? ipAddress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
