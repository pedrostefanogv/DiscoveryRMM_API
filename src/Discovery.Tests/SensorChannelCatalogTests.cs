using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// O catalogo de canais e o extrator tem de andar juntos. Se um canal existir no catalogo
/// mas o extrator nao o emitir, a UI mostra um grafico permanentemente vazio; se o extrator
/// emitir fora do catalogo, o dado existe mas nenhuma tela consegue rotula-lo. O caso
/// power_on_hours ja aconteceu: o agente coletava, o DTO carregava e o canal nao existia
/// em lugar nenhum do servidor.
/// </summary>
[TestFixture]
public class SensorChannelCatalogTests
{
    private static readonly Dictionary<SensorType, string[]> IntentionallyUnlabeled = new()
    {
        // start_pending e diagnostico do estado transitorio do servico; nao vira grafico.
        [SensorType.Service] = new[] { "start_pending" }
    };

    [Test]
    public void EveryCatalogChannel_IsEmittedByTheExtractor()
    {
        var problems = new List<string>();
        foreach (var (type, channels) in CatalogByType())
        {
            var emitted = EmittedChannels(type);
            foreach (var channel in channels)
            {
                if (!emitted.Contains(channel.Key))
                    problems.Add(type + ": catalogo declara " + channel.Key + " mas o extrator nao emite");
            }
        }
        Assert.That(problems, Is.Empty, string.Join(" | ", problems));
    }

    [Test]
    public void EveryEmittedChannel_IsInCatalogOrDocumented()
    {
        var problems = new List<string>();
        foreach (var (type, channels) in CatalogByType())
        {
            var allowed = new HashSet<string>(channels.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            if (IntentionallyUnlabeled.TryGetValue(type, out var extra))
                foreach (var e in extra) allowed.Add(e);

            foreach (var emitted in EmittedChannels(type))
            {
                if (!allowed.Contains(emitted))
                    problems.Add(type + ": extrator emite " + emitted + " sem entrada no catalogo");
            }
        }
        Assert.That(problems, Is.Empty, string.Join(" | ", problems));
    }

    [Test]
    public void EverySensorType_HasACatalogEntry()
    {
        var missing = Enum.GetValues<SensorType>()
            .Where(t => t != SensorType.Script)
            .Where(t => CatalogChannels(t).Count == 0)
            .ToList();
        Assert.That(missing, Is.Empty, "Tipos sem catalogo: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryType_HasExactlyOnePrimaryChannel()
    {
        var problems = new List<string>();
        foreach (var type in Enum.GetValues<SensorType>())
        {
            var channels = CatalogChannels(type);
            if (channels.Count == 0) continue;
            var primaries = channels.Count(c => c.IsPrimary);
            if (primaries != 1) problems.Add(type + ": " + primaries + " canais primarios");
        }
        Assert.That(problems, Is.Empty, string.Join(" | ", problems));
    }

    [Test]
    public void PrimaryChannel_IsActuallyEmitted()
    {
        var problems = new List<string>();
        foreach (var (type, channels) in CatalogByType())
        {
            var primary = channels.FirstOrDefault(c => c.IsPrimary);
            if (primary is null) continue;
            if (!EmittedChannels(type).Contains(primary.Key))
                problems.Add(type + ": primario " + primary.Key + " nao e emitido");
        }
        Assert.That(problems, Is.Empty, string.Join(" | ", problems));
    }

    /// <summary>
    /// Canal declarado pelo usuário vence o catálogo embutido, para QUALQUER tipo — não só
    /// Script. Antes só Script honrava ChannelsJson, então um canal primário declarado num
    /// sensor nativo era descartado em silêncio pelo servidor.
    /// </summary>
    [Test]
    public void DeclaredPrimaryChannel_WinsOverBuiltInCatalog_ForNativeType()
    {
        var definition = new SensorDefinition
        {
            SensorType = SensorType.Ping,
            ChannelsJson = "[{\"key\":\"latency_avg_ms\",\"label\":\"Latencia\",\"isPrimary\":false}," +
                            "{\"key\":\"jitter_ms\",\"label\":\"Jitter\",\"isPrimary\":true}]"
        };

        var resolved = SensorChannelCatalog.Resolve(definition);
        Assert.That(resolved, Has.Count.EqualTo(2), "canais declarados devem ser preservados");
        Assert.That(resolved.Single(c => c.IsPrimary).Key, Is.EqualTo("jitter_ms"));
    }

    /// <summary>Sem canais declarados, o catálogo embutido continua sendo o padrão.</summary>
    [Test]
    public void UndeclaredChannels_FallBackToBuiltInCatalog()
    {
        var definition = new SensorDefinition { SensorType = SensorType.Ping, ChannelsJson = null };
        var resolved = SensorChannelCatalog.Resolve(definition);
        Assert.That(resolved.Single(c => c.IsPrimary).Key, Is.EqualTo("latency_avg_ms"));
    }
    private static IReadOnlyList<SensorChannelDto> CatalogChannels(SensorType type)
    {
        var definition = new SensorDefinition { SensorType = type };
        return SensorChannelCatalog.Resolve(definition);
    }

    private static IEnumerable<(SensorType Type, IReadOnlyList<SensorChannelDto> Channels)> CatalogByType()
        => Enum.GetValues<SensorType>()
            .Select(t => (Type: t, Channels: CatalogChannels(t)))
            .Where(x => x.Channels.Count > 0);

    /// <summary>
    /// Canais que o extrator produz para um tipo. Chamamos o DeriveChannels real com uma
    /// medida totalmente preenchida, para que nenhum canal seja omitido por falta de valor:
    /// e exatamente essa omissao que queremos detectar.
    /// </summary>
    private static HashSet<string> EmittedChannels(SensorType type)
    {
        var definition = new SensorDefinition { SensorType = type };
        var derived = SensorEvaluator.DeriveChannels(definition, FullyPopulatedMeasure(), null);
        return new HashSet<string>(derived.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private static AgentSensorMeasureDto FullyPopulatedMeasure()
    {
        var m = new AgentSensorMeasureDto();
        m.FreePercent = 50; m.UsedPercent = 50; m.TotalBytes = 1; m.FreeBytes = 1;
        m.ServiceState = "Running"; m.ProcessState = "Running"; m.Running = true;
        m.ProcessCount = 1; m.WorkingSetBytes = 1;
        m.Replies = 1; m.Sent = 1; m.AvgRttMs = 1; m.LatencyMinMs = 1; m.LatencyMaxMs = 1;
        m.JitterMs = 1; m.LossPercent = 1;
        m.StatusCode = 200; m.DnsMs = 1; m.ConnectMs = 1; m.TlsMs = 1; m.TtfbMs = 1; m.TotalMs = 1; m.Bytes = 1;
        m.DownloadMbps = 1; m.UploadMbps = 1; m.UptimeSeconds = 1; m.ResolveMs = 1;
        m.RxBps = 1; m.TxBps = 1; m.ReadBps = 1; m.WriteBps = 1; m.BusyPercent = 1; m.DiskLatencyMs = 1;
        m.EventCount = 1; m.EventWindowMinutes = 60;
        m.SmartStatus = "Healthy"; m.TemperatureC = 40; m.PowerOnHours = 1; m.ReallocatedSectors = 1;
        m.DaysToExpiry = 30; m.CertificateSubject = "CN=x";
        m.DefenderEnabled = true; m.DefenderRealTimeEnabled = true; m.SignatureAgeDays = 1;
        m.BitLockerStatus = "On"; m.BitLockerEncryptionPercent = 100;
        return m;
    }
}