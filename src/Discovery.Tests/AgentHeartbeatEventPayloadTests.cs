using System.Reflection;
using Discovery.Core.DTOs;
using Discovery.Infrastructure.Messaging;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guard do evento de dashboard "AgentHeartbeat" — o caminho de TEMPO REAL do
/// painel.
///
/// Histórico (achado em teste de campo): o payload do evento é montado com uma
/// WHITELIST de campos. A bateria foi adicionada ao DTO e ao cache Redis (que
/// alimenta o polling HTTP), mas esquecida aqui. Resultado: o operador
/// desplugava o notebook e a listagem/detalhe continuavam mostrando "Na tomada"
/// — o valor só mudava no próximo refetch das queries de agente, que é de 5
/// minutos.
///
/// O primeiro teste percorre o DTO por REFLEXÃO: qualquer campo novo que não
/// seja projetado aqui (nem esteja na lista de outro evento) faz o teste falhar,
/// em vez de degradar silenciosamente o tempo real.
/// </summary>
[TestFixture]
public class AgentHeartbeatEventPayloadTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    /// <summary>Campos de outros eventos: PeerId/Addrs/Port viajam no peer.online.</summary>
    private static readonly string[] CamposDeOutroEvento = ["PeerId", "Addrs", "Port"];

    private static object Build(AgentHeartbeat heartbeat)
        => NatsAgentMessaging.BuildHeartbeatEventPayload(heartbeat, AgentId, Guid.NewGuid(), Guid.NewGuid());

    private static Dictionary<string, object?> PayloadProperties(object payload)
        => payload.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p.GetValue(payload), StringComparer.OrdinalIgnoreCase);

    [Test]
    public void PayloadDoEvento_CobreTodosOsCamposDoHeartbeat()
    {
        var payload = PayloadProperties(Build(new AgentHeartbeat(AgentId)));

        var faltando = typeof(AgentHeartbeat).GetProperties()
            .Select(property => property.Name)
            .Where(nome => !CamposDeOutroEvento.Contains(nome, StringComparer.OrdinalIgnoreCase))
            .Where(nome => !payload.ContainsKey(nome))
            .ToList();

        Assert.That(
            faltando,
            Is.Empty,
            "Campos do AgentHeartbeat ausentes no evento de tempo real (o painel só os veria no polling de 5 min): "
                + string.Join(", ", faltando));
    }

    [Test]
    public void PayloadDoEvento_LevaEnergiaDaBateria()
    {
        var payload = PayloadProperties(Build(new AgentHeartbeat(
            AgentId,
            HasBattery: true,
            BatteryPercent: 63,
            BatteryOnAc: false,
            BatteryCharging: false)));

        Assert.Multiple(() =>
        {
            Assert.That(payload["HasBattery"], Is.True);
            Assert.That(payload["BatteryPercent"], Is.EqualTo(63));
            Assert.That(payload["BatteryOnAc"], Is.False,
                "batteryOnAc=false é resposta positiva (a máquina está NA BATERIA) e precisa chegar ao tempo real");
            Assert.That(payload["BatteryCharging"], Is.False);
        });
    }

    /// <summary>
    /// Leitura incerta não pode virar false no caminho de tempo real: o painel
    /// esconderia a bateria em vez de dizer que a máquina está na tomada.
    /// </summary>
    [Test]
    public void PayloadDoEvento_SemEnergiaReportada_MantemNulo()
    {
        var payload = PayloadProperties(Build(new AgentHeartbeat(AgentId)));

        Assert.Multiple(() =>
        {
            Assert.That(payload["HasBattery"], Is.Null);
            Assert.That(payload["BatteryPercent"], Is.Null);
            Assert.That(payload["BatteryOnAc"], Is.Null);
            Assert.That(payload["BatteryCharging"], Is.Null);
        });
    }

    /// <summary>Identidade do evento continua saindo como antes da mudança.</summary>
    [Test]
    public void PayloadDoEvento_MantemIdentidadeEStatus()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var payload = PayloadProperties(
            NatsAgentMessaging.BuildHeartbeatEventPayload(new AgentHeartbeat(AgentId), AgentId, clientId, siteId));

        Assert.Multiple(() =>
        {
            Assert.That(payload["AgentId"], Is.EqualTo(AgentId));
            Assert.That(payload["ClientId"], Is.EqualTo(clientId));
            Assert.That(payload["SiteId"], Is.EqualTo(siteId));
            Assert.That(payload["Status"], Is.EqualTo("Online"));
        });
    }
}
