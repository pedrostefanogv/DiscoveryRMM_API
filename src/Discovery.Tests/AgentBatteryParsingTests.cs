using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Bateria no inventário de hardware.
///
/// Contexto: o agente coleta bateria desde sempre, mas o dado só existia dentro
/// do <c>inventoryRaw</c> (JSON cru) — não havia lista estruturada, então o
/// painel não tinha como mostrar carga, tomada nem saúde da bateria.
///
/// O nome da propriedade MUDA entre as duas fontes: <c>battery</c> no
/// inventoryRaw (herdado do modelo Go) e <c>batteries</c> no envelope de
/// componentes. Estes testes travam as duas leituras e a precedência do merge.
/// </summary>
public class AgentBatteryParsingTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");
    private static readonly DateTime CollectedAt = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement? Incoming(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public void InventoryRaw_BatteryViraListaEstruturada()
    {
        var raw = """
        {
          "battery": [
            {
              "manufacturer": "SMP",
              "model": "L19M3PD6",
              "serialNumber": "1234",
              "chemistry": "LiP",
              "state": "Discharging",
              "onAc": false,
              "present": true,
              "charging": false,
              "charged": false,
              "percentRemaining": 63,
              "designedCapacityMAh": 45000,
              "maxCapacityMAh": 41000,
              "currentCapacityMAh": 25800,
              "cycleCount": 212
            }
          ]
        }
        """;

        var components = HardwareInventoryParser.MergeComponents(null, raw, null, AgentId, CollectedAt);

        Assert.That(components, Is.Not.Null);
        Assert.That(components!.Batteries, Has.Count.EqualTo(1));

        var battery = components.Batteries[0];
        Assert.Multiple(() =>
        {
            Assert.That(battery.Manufacturer, Is.EqualTo("SMP"));
            Assert.That(battery.Model, Is.EqualTo("L19M3PD6"));
            Assert.That(battery.State, Is.EqualTo("Discharging"));
            Assert.That(battery.OnAC, Is.False, "na bateria: onAc=false é resposta positiva, distinguível de null");
            Assert.That(battery.Present, Is.True);
            Assert.That(battery.PercentRemaining, Is.EqualTo(63));
            Assert.That(battery.DesignedCapacityMAh, Is.EqualTo(45000));
            Assert.That(battery.MaxCapacityMAh, Is.EqualTo(41000));
            Assert.That(battery.CycleCount, Is.EqualTo(212));
        });
    }

    [Test]
    public void PayloadBatteries_VenceOInventarioRaw()
    {
        var incoming = Incoming("""
        {
          "batteries": [
            { "manufacturer": "Panasonic", "percentRemaining": 88, "onAc": true, "charging": true }
          ]
        }
        """);

        var raw = """{ "battery": [ { "manufacturer": "SMP", "percentRemaining": 10 } ] }""";

        var components = HardwareInventoryParser.MergeComponents(incoming, raw, null, AgentId, CollectedAt);

        Assert.That(components!.Batteries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(components.Batteries[0].Manufacturer, Is.EqualTo("Panasonic"));
            Assert.That(components.Batteries[0].PercentRemaining, Is.EqualTo(88));
            Assert.That(components.Batteries[0].OnAC, Is.True);
            Assert.That(components.Batteries[0].Charging, Is.True);
        });
    }

    /// <summary>
    /// Sync parcial (ex.: só portas em escuta) não pode apagar a bateria já
    /// armazenada — mesma regra das outras listas.
    /// </summary>
    [Test]
    public void SyncParcial_SemBateria_PreservaListaArmazenada()
    {
        var existing = new AgentHardwareComponents
        {
            Batteries =
            [
                new AgentBatteryInfo
                {
                    Id = Guid.NewGuid(),
                    AgentId = AgentId,
                    Manufacturer = "SMP",
                    PercentRemaining = 55,
                    OnAC = true,
                    Present = true,
                    CollectedAt = CollectedAt.AddDays(-1)
                }
            ]
        };

        var incoming = Incoming("""{ "listeningPorts": [ { "processName": "svc", "port": 80 } ] }""");
        var components = HardwareInventoryParser.MergeComponents(incoming, null, existing, AgentId, CollectedAt);

        Assert.That(components!.Batteries, Has.Count.EqualTo(1));
        Assert.That(components.Batteries[0].Manufacturer, Is.EqualTo("SMP"));
    }

    /// <summary>
    /// 255 é o sentinela de "desconhecido" em SYSTEM_POWER_STATUS: não pode
    /// chegar ao painel como "255%".
    /// </summary>
    [Test]
    public void PercentualForaDaFaixa_ViraZero()
    {
        var raw = """{ "battery": [ { "percentRemaining": 255, "onAc": true } ] }""";

        var components = HardwareInventoryParser.MergeComponents(null, raw, null, AgentId, CollectedAt);

        Assert.That(components!.Batteries[0].PercentRemaining, Is.Zero);
        Assert.That(components.Batteries[0].OnAC, Is.True, "o estado de tomada continua confiável");
    }

    /// <summary>
    /// Um inventário que SÓ tem bateria não pode ser descartado como "inventário
    /// vazio" — era o risco de esquecer a lista nova na checagem.
    /// </summary>
    [Test]
    public void InventarioComApenasBateria_NaoEhDescartado()
    {
        var raw = """{ "battery": [ { "percentRemaining": 40, "onAc": false } ] }""";

        var components = HardwareInventoryParser.TryBuildFromInventoryRaw(raw, AgentId, CollectedAt);

        Assert.That(components, Is.Not.Null, "lista de bateria conta como inventário presente");
        Assert.That(components!.Batteries, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Desktop/VM e agente antigo: nenhuma bateria, lista vazia, sem lançar.
    /// </summary>
    [Test]
    public void SemBateria_ListaVaziaSemErro()
    {
        var components = HardwareInventoryParser.MergeComponents(null, """{ "disks": [] }""", null, AgentId, CollectedAt);

        Assert.That(components, Is.Not.Null);
        Assert.That(components!.Batteries, Is.Empty);
    }
}
