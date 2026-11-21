using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// BUG 2026-11-21 (tupa.pstefano.com.br): o validador de conexao NATS testava
/// "wss://&lt;host&gt;:4222" — porta que NUNCA e publicada em producao. O WebSocket
/// do NATS e servido pelo nginx em 443 no caminho /nats/ (a 4222 e o protocolo
/// NATS nativo, fechado para a internet de proposito). Consequencia: "Testar
/// Conexao" na tela de Servidor NATS acusava falha mesmo com o realtime
/// funcionando (o handshake wss://host/nats/ devolve 101).
///
/// Estes testes travam a ORDEM das sondagens, para que a porta 4222 nunca volte
/// a ser a unica tentativa.
/// </summary>
public class NatsConnectionValidatorTests
{
    /// <summary>
    /// Sem rede real, o validador falha; mas a mensagem de erro revela quais
    /// endpoints foram sondados. E o contrato que este teste protege.
    /// </summary>
    [Test]
    public async Task HostOnly_ProbesWebSocketPathOn443_NotOnlyPort4222()
    {
        var validator = new NatsConnectionValidator();

        // Host inexistente: a resolucao falha rapido e nao depende de rede externa.
        var (isValid, errors) = await validator.ValidateConnectionAsync(
            "host-invalido-para-teste.invalid", null, null, CancellationToken.None);

        Assert.That(isValid, Is.False);
        var message = string.Join(" | ", errors);

        Assert.That(message, Does.Contain("wss://host-invalido-para-teste.invalid/nats/"),
            "o validador deve sondar o caminho WebSocket publicado pelo nginx (443 + /nats/)");
    }

    [Test]
    public async Task HostOnly_StillProbesNativeNatsForDirectAccess()
    {
        var validator = new NatsConnectionValidator();

        var (_, errors) = await validator.ValidateConnectionAsync(
            "host-invalido-para-teste.invalid", null, null, CancellationToken.None);

        var message = string.Join(" | ", errors);
        Assert.That(message, Does.Contain("nats://host-invalido-para-teste.invalid:4222"),
            "o acesso NATS nativo direto continua sendo sondado primeiro");
    }

    [Test]
    public async Task NatsScheme_AlsoProbesWebSocketPath()
    {
        var validator = new NatsConnectionValidator();

        var (_, errors) = await validator.ValidateConnectionAsync(
            "nats://host-invalido-para-teste.invalid:4222", null, null, CancellationToken.None);

        var message = string.Join(" | ", errors);
        Assert.That(message, Does.Contain("wss://host-invalido-para-teste.invalid/nats/"),
            "nats:// tambem deve cair para o WebSocket em /nats/ quando o nativo falha");
    }

    [Test]
    public async Task EmptyHost_IsStillAValidationError()
    {
        var validator = new NatsConnectionValidator();

        var (isValid, errors) = await validator.ValidateConnectionAsync(
            "   ", null, null, CancellationToken.None);

        Assert.That(isValid, Is.False);
        Assert.That(string.Join(" ", errors), Does.Contain("cannot be empty"));
    }
}
