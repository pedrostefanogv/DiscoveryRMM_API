using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Enums;

namespace Discovery.Tests;

/// <summary>
/// Validação do bloco "webrtc" do payload de start de sessão remota.
///
/// O bloco é ADITIVO e opcional: ausente, o agent segue no NATS. Quando presente,
/// validamos aqui para o operador receber erro CLARO em vez de a sessão subir com
/// ICE inválido e falhar 15 s depois, na frente do cliente.
/// </summary>
[TestFixture]
public class RemoteSessionWebRtcPayloadValidatorTests
{
    private static SpecialCommandPayloadValidator Validator() => new();

    private static string StartPayload(string webrtcBlock) => $$"""
        {
          "action": "start",
          "sessionId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
          "kind": "screen",
          "transport": "webrtc",
          "quality": "high",
          "codec": "jpeg",
          "imageQuality": 70,
          "maxFps": 15,
          "durationMinutes": 30,
          "expiresAtUtc": "2026-11-21T00:00:00.0000000Z",
          "webrtc": {{webrtcBlock}}
        }
        """;

    // ── Ausência do bloco é válida (é o caso do NATS) ───────────────────

    [Test]
    public void Bloco_ausente_e_aceito()
    {
        var payload = """
            {
              "action": "stop",
              "sessionId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
            }
            """;

        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStop, payload, out _, out var error),
            Is.True, error);
    }

    [Test]
    public void Bloco_null_e_aceito()
    {
        var payload = """
            {
              "action": "start",
              "sessionId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
              "kind": "screen",
              "transport": "nats",
              "quality": "high",
              "codec": "jpeg",
              "durationMinutes": 30,
              "webrtc": null
            }
            """;

        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStart, payload, out _, out var error),
            Is.True, error);
    }

    // ── Formas válidas ──────────────────────────────────────────────────

    [Test]
    public void IceServers_com_urls_em_array_e_aceito()
    {
        var payload = StartPayload("""
            {
              "enabled": true,
              "kinds": ["screen", "rdp"],
              "iceServers": [
                { "urls": ["stun:stun.l.google.com:19302", "stun:stun.cloudflare.com:3478"] },
                { "urls": ["turn:turn.exemplo.com:3478"], "username": "u", "credential": "p" }
              ],
              "fallbackToNats": true,
              "failedTimeoutSeconds": 15
            }
            """);

        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStart, payload, out _, out var error),
            Is.True, error);
    }

    [Test]
    public void IceServers_com_urls_em_string_e_aceito()
    {
        // Tolerância deliberada: o navegador aceita as duas formas, e o agente
        // também (o Breeze faz igual). Recusar string quebraria a negociação em
        // silêncio se a serialização do console mudasse.
        var payload = StartPayload("""
            {
              "enabled": true,
              "iceServers": [ { "urls": "stun:stun.l.google.com:19302" } ]
            }
            """);

        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStart, payload, out _, out var error),
            Is.True, error);
    }

    [Test]
    public void Turns_e_aceito()
    {
        var payload = StartPayload("""
            {
              "enabled": true,
              "iceServers": [ { "urls": ["turns:turn.exemplo.com:5349"] } ]
            }
            """);

        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStart, payload, out _, out var error),
            Is.True, error);
    }

    [Test]
    public void Bloco_vazio_e_aceito()
    {
        Assert.That(Validator().TryNormalize(CommandType.RemoteSessionStart, StartPayload("{}"), out _, out var error),
            Is.True, error);
    }

    // ── Formas inválidas ────────────────────────────────────────────────

    [Test]
    public void Bloco_que_nao_e_objeto_e_recusado()
    {
        // JSON string em vez de objeto: o bloco existe mas com o tipo errado.
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("\"texto\""), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("webrtc"));
        });
    }

    [Test]
    public void Enabled_nao_booleano_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"enabled":"sim"}"""), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("webrtc.enabled"));
        });
    }

    [Test]
    public void IceServers_que_nao_e_array_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"iceServers":"stun:x"}"""), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("iceServers"));
        });
    }

    [Test]
    public void IceServer_sem_urls_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"iceServers":[{"username":"u"}]}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("urls"));
        });
    }

    [Test]
    public void IceUrl_sem_esquema_valido_e_recusado()
    {
        // "stun.l.google.com:19302" sem o esquema é o erro mais comum de quem
        // digita à mão — e produziria uma falha de ICE silenciosa no cliente.
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart,
            StartPayload("""{"iceServers":[{"urls":["stun.l.google.com:19302"]}]}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("stun:"));
        });
    }

    [Test]
    public void IceUrl_com_esquema_desconhecido_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart,
            StartPayload("""{"iceServers":[{"urls":["http://exemplo.com"]}]}"""),
            out _, out var error);

        Assert.That(ok, Is.False, error);
    }

    [Test]
    public void IceUrl_vazia_e_recusada()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart,
            StartPayload("""{"iceServers":[{"urls":["  "]}]}"""),
            out _, out var error);

        Assert.That(ok, Is.False, error);
    }

    [Test]
    public void Timeout_fora_da_faixa_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"failedTimeoutSeconds":9999}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("failedTimeoutSeconds"));
        });
    }

    [Test]
    public void Timeout_zero_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"keepAliveIntervalSeconds":0}"""),
            out _, out var error);

        Assert.That(ok, Is.False, error);
    }

    [Test]
    public void H264_ligado_e_recusado()
    {
        // Decisão travada por patente: recusar explicitamente é melhor que aceitar
        // e desligar em silêncio (o operador acharia que ligou).
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"h264Enabled":true}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("H.264"));
        });
    }

    [Test]
    public void FallbackToNats_nao_booleano_e_recusado()
    {
        var ok = Validator().TryNormalize(
            CommandType.RemoteSessionStart, StartPayload("""{"fallbackToNats":"talvez"}"""),
            out _, out var error);

        Assert.That(ok, Is.False, error);
    }
}
