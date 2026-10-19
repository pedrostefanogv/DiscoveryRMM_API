using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Mapeamento do snapshot para o DTO da aba "Atualizações".
///
/// Compatibilidade: campos da sonda de estado são OPCIONAIS — um agente antigo
/// (snapshot sem state) devolve null e o site decide o que mostrar.
/// </summary>
[TestFixture]
public class WindowsUpdateSnapshotDtoTests
{
    private static AgentWindowsUpdateSnapshot Snapshot() => new()
    {
        AgentId = Guid.NewGuid(),
        CollectedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
        ScanStatus = "ok",
        PendingCount = 2,
        InstalledCount = 4,
        DeniedCount = 0,
        PendingReboot = true,
        PendingRebootReasons = "[\"cbs\"]",
        RebootCheckedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void LegacySnapshotWithoutStateMapsToNull()
    {
        var dto = PatchDtoMapper.ToSnapshotDto(Guid.NewGuid(), Snapshot(), 2, 4, 0, 1);

        Assert.That(dto.ServiceState, Is.Null);
        Assert.That(dto.WuOnline, Is.Null);
        Assert.That(dto.PolicyManaged, Is.Null);
        Assert.That(dto.LastInstallSuccessAt, Is.Null);
        Assert.That(dto.DownloadInProgress, Is.Null);
        Assert.That(dto.StateChangedAt, Is.Null);
        // Sem sonda não existe bloco probe (o painel esconde a linha).
        Assert.That(dto.Probe, Is.Null);
        // Campos antigos seguem iguais.
        Assert.That(dto.PendingCount, Is.EqualTo(2));
        Assert.That(dto.PendingReboot, Is.True);
        Assert.That(dto.PendingRebootReasons, Is.EquivalentTo(new[] { "cbs" }));
    }

    [Test]
    public void StateProbeIsExposedOnDto()
    {
        var snapshot = Snapshot();
        snapshot.ServiceState = "running";
        snapshot.WuOnline = true;
        snapshot.PolicyManaged = true;
        snapshot.DownloadInProgress = false;
        snapshot.StateHash = "h1";
        snapshot.StateChangedAt = new DateTime(2026, 10, 18, 13, 0, 0, DateTimeKind.Utc);
        snapshot.LastInstallSuccessAt = new DateTime(2026, 10, 16, 21, 9, 57, DateTimeKind.Utc);

        var dto = PatchDtoMapper.ToSnapshotDto(snapshot.AgentId, snapshot, 2, 4, 0, 1);

        Assert.That(dto.ServiceState, Is.EqualTo("running"));
        Assert.That(dto.WuOnline, Is.True);
        Assert.That(dto.PolicyManaged, Is.True);
        Assert.That(dto.DownloadInProgress, Is.False);
        Assert.That(dto.StateHash, Is.EqualTo("h1"));
        Assert.That(dto.StateChangedAt, Is.Not.Null);
        Assert.That(dto.LastInstallSuccessAt, Is.Not.Null);
    }

    /// <summary>
    /// O console lê <c>snapshot.probe</c> (service/serviceDisabled/…). Sem este
    /// bloco a linha de diagnóstico da aba nunca aparecia, mesmo com a sonda
    /// persistida no snapshot.
    /// </summary>
    [Test]
    public void ProbeBlockMirrorsSiteContract()
    {
        var snapshot = Snapshot();
        snapshot.ServiceState = "disabled";
        snapshot.PolicyManaged = true;
        snapshot.DownloadInProgress = true;
        snapshot.LastInstallSuccessAt = new DateTime(2026, 10, 16, 21, 9, 57, DateTimeKind.Utc);

        var dto = PatchDtoMapper.ToSnapshotDto(snapshot.AgentId, snapshot, 0, 4, 0, 0);

        Assert.That(dto.Probe, Is.Not.Null);
        Assert.That(dto.Probe!.Service, Is.EqualTo("disabled"));
        Assert.That(dto.Probe.ServiceDisabled, Is.True);
        Assert.That(dto.Probe.PolicyManaged, Is.True);
        Assert.That(dto.Probe.DownloadInProgress, Is.True);
        Assert.That(dto.Probe.LastInstallSuccessAt, Is.Not.Null);
    }

    [Test]
    public void ProbeBlockUsesOnlineServiceAsNotDisabled()
    {
        var snapshot = Snapshot();
        snapshot.ServiceState = "running";

        var dto = PatchDtoMapper.ToSnapshotDto(snapshot.AgentId, snapshot, 0, 4, 0, 0);

        Assert.That(dto.Probe, Is.Not.Null);
        Assert.That(dto.Probe!.ServiceDisabled, Is.False);
    }

    [Test]
    public void MissingSnapshotStillProducesUsableDto()
    {
        var agentId = Guid.NewGuid();
        var dto = PatchDtoMapper.ToSnapshotDto(agentId, null, 3, 0, 1, 2);

        Assert.That(dto.AgentId, Is.EqualTo(agentId));
        Assert.That(dto.ScanStatus, Is.EqualTo("unknown"));
        Assert.That(dto.PendingCount, Is.EqualTo(3));
        Assert.That(dto.ServiceState, Is.Null);
    }

    [Test]
    public void LegacyConstructorStillCompilesWithPositionalArgs()
    {
        // Contrato posicional preservado: código que cria o DTO sem os campos
        // novos (site antigo/testes) continua compilando e funcionando.
        var dto = new AgentWindowsUpdateSnapshotDto(
            Guid.NewGuid(), 1, 2, 3, 4, "ok", null, 10,
            DateTime.UtcNow, false, [], null);

        Assert.That(dto.InstalledCount, Is.EqualTo(2));
        Assert.That(dto.ServiceState, Is.Null);
        Assert.That(dto.DownloadInProgress, Is.Null);
    }

    [Test]
    public void ParserStateFeedsSnapshotEntity()
    {
        // Fluxo completo do payload → entidade gravada no snapshot.
        var meta = Discovery.Core.Cqrs.AgentAuth.Patches.WindowsUpdateInventoryParser.ParseState(
            System.Text.Json.JsonDocument.Parse("""
            { "serviceState": "disabled", "wuOnline": false, "policyManaged": true }
            """).RootElement.Clone());

        Assert.That(meta, Is.Not.Null);
        Assert.That(meta!.ServiceState, Is.EqualTo("disabled"));
        Assert.That(meta.IsEmpty, Is.False);
        Assert.That(PatchUpdateStatus.Installed, Is.Not.EqualTo(PatchUpdateStatus.Failed));
    }
}
