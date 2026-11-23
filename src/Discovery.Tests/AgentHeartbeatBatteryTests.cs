using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Energia (bateria × tomada) no heartbeat.
///
/// O heartbeat é o canal FREQUENTE (≈15 s) e o inventário roda a cada 24 h:
/// sem estes campos o painel mostraria "na tomada" para um notebook já
/// desplugado. Estes testes travam o caminho cache → AgentDto.
/// </summary>
public class AgentHeartbeatBatteryTests
{
    private static Agent BuildAgent() => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Hostname = "NOTE-ANA",
        OperatingSystem = "Windows 11 Pro",
        Status = Discovery.Core.Enums.AgentStatus.Online
    };

    [Test]
    public void HeartbeatComEnergia_ChegaNoHeartbeatMetricsDto()
    {
        var hb = new HeartbeatCacheEntry
        {
            AgentId = Guid.NewGuid(),
            LastHeartbeatAt = DateTime.UtcNow,
            HasBattery = true,
            BatteryPercent = 63,
            BatteryOnAc = false,
            BatteryCharging = false
        };

        var dto = AgentQueryHelper.MapToDto(BuildAgent(), hb);

        Assert.That(dto.HeartbeatMetrics, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto.HeartbeatMetrics!.HasBattery, Is.True);
            Assert.That(dto.HeartbeatMetrics.BatteryPercent, Is.EqualTo(63));
            Assert.That(dto.HeartbeatMetrics.BatteryOnAc, Is.False);
            Assert.That(dto.HeartbeatMetrics.BatteryCharging, Is.False);
        });
    }

    /// <summary>
    /// Desktop: hasBattery=false é resposta positiva e precisa chegar ao painel
    /// para ele esconder o bloco de bateria sem depender de heurística.
    /// </summary>
    [Test]
    public void Desktop_HasBatteryFalseChegaNoDto()
    {
        var hb = new HeartbeatCacheEntry
        {
            AgentId = Guid.NewGuid(),
            LastHeartbeatAt = DateTime.UtcNow,
            HasBattery = false,
            BatteryOnAc = true
        };

        var dto = AgentQueryHelper.MapToDto(BuildAgent(), hb);

        Assert.Multiple(() =>
        {
            Assert.That(dto.HeartbeatMetrics!.HasBattery, Is.False);
            Assert.That(dto.HeartbeatMetrics.BatteryPercent, Is.Null, "sem bateria não há percentual");
            Assert.That(dto.HeartbeatMetrics.BatteryOnAc, Is.True);
        });
    }

    /// <summary>
    /// Agente antigo/leitura incerta: tudo null. O painel esconde o bloco —
    /// jamais interpreta ausência como "sem bateria" nem como "na bateria".
    /// </summary>
    [Test]
    public void SemEnergiaReportada_TudoNulo()
    {
        var hb = new HeartbeatCacheEntry
        {
            AgentId = Guid.NewGuid(),
            LastHeartbeatAt = DateTime.UtcNow
        };

        var dto = AgentQueryHelper.MapToDto(BuildAgent(), hb);

        Assert.Multiple(() =>
        {
            Assert.That(dto.HeartbeatMetrics!.HasBattery, Is.Null);
            Assert.That(dto.HeartbeatMetrics.BatteryPercent, Is.Null);
            Assert.That(dto.HeartbeatMetrics.BatteryOnAc, Is.Null);
            Assert.That(dto.HeartbeatMetrics.BatteryCharging, Is.Null);
        });
    }

    /// <summary>Sem heartbeat nenhum, o DTO não inventa métricas.</summary>
    [Test]
    public void SemHeartbeat_HeartbeatMetricsNulo()
    {
        var dto = AgentQueryHelper.MapToDto(BuildAgent(), null);

        Assert.That(dto.HeartbeatMetrics, Is.Null);
    }
}
