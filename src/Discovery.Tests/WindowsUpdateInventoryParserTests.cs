using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Patches;
using Discovery.Core.Entities;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class WindowsUpdateInventoryParserTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Test]
    public void ParsesCanonicalGuidKey()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [{
          "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556666:12",
          "updateId": "3C3F1F1A-1111-2222-3333-444455556666",
          "revision": 12,
          "kbIds": ["5034441", "kb5034441"],
          "title": "2024-01 Security Update",
          "severity": "Critical",
          "classification": "Security Updates",
          "isPending": true,
          "requiresReboot": true
        }]
        """));

        Assert.That(entries, Has.Count.EqualTo(1));
        var entry = entries[0];
        Assert.That(entry.UpdateKey, Is.EqualTo("guid:3c3f1f1a-1111-2222-3333-444455556666:12"));
        Assert.That(entry.UpdateId, Is.EqualTo("3c3f1f1a-1111-2222-3333-444455556666"));
        Assert.That(entry.Revision, Is.EqualTo(12));
        Assert.That(entry.KbIds, Does.Contain("KB5034441"));
        Assert.That(entry.IsPending, Is.True);
        Assert.That(entry.RequiresReboot, Is.True);
    }

    [Test]
    public void ParsesWaaSFeatureUpgradeSyntheticEntry()
    {
        // Item sintetizado pelo agente a partir do WaaSFeatureAssessment
        // (feature update nao e enumerado pelo WUA no Windows 11).
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [{
          "updateKey": "guid:11111111-2222-3333-4444-555555555555:1",
          "updateId": "11111111-2222-3333-4444-555555555555",
          "revision": 1,
          "title": "Atualizacao de versao do Windows disponivel (versao atual 25H2)",
          "description": "WaaS feature assessment: status=1 impact=2 daysOutOfDate=10.",
          "classification": "Upgrades",
          "updateType": "feature",
          "severity": "Moderate",
          "severitySource": "derived",
          "isPending": true,
          "isInstalled": false,
          "source": "waas"
        }]
        """));

        Assert.That(entries, Has.Count.EqualTo(1));
        var entry = entries[0];
        Assert.That(entry.Source, Is.EqualTo("waas"));
        Assert.That(entry.UpdateType, Is.EqualTo("feature"));
        Assert.That(entry.Classification, Is.EqualTo("Upgrades"));
        Assert.That(entry.IsPending, Is.True);
        Assert.That(entry.IsInstalled, Is.False);
    }

    [Test]
    public void ParsesSeveritySourceAndRejectsUnknownValues()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [
          {
            "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556666:1",
            "title": "2024-01 Cumulative Update",
            "severity": "Important",
            "severitySource": "Derived"
          },
          {
            "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556667:1",
            "title": "Sem rating",
            "severitySource": "inventado"
          },
          {
            "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556668:1",
            "title": "Sem proveniencia",
            "severity": "Critical"
          }
        ]
        """));

        Assert.That(entries, Has.Count.EqualTo(3));
        Assert.That(entries[0].SeveritySource, Is.EqualTo("derived"));
        Assert.That(entries[1].SeveritySource, Is.Null, "valor fora da allowlist deve virar null");
        Assert.That(entries[2].SeveritySource, Is.Null, "agente antigo (campo ausente) deve ficar null");
    }

    [Test]
    public void SeveritySourceIsTruncatedAtColumnLimit()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [{
          "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556666:1",
          "title": "x",
          "severitySource": "vendor-derived-unknown-vendor"
        }]
        """));

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].SeveritySource, Is.Null, "valor maior que a coluna nao entra (nem truncado)");
    }

    [Test]
    public void DerivesKeyWhenMissing()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [{
          "updateId": "3c3f1f1a-1111-2222-3333-444455556666",
          "revision": 3,
          "kbIds": ["5034100"],
          "title": "Update",
          "isPending": true
        }]
        """));

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].UpdateKey, Is.EqualTo("guid:3c3f1f1a-1111-2222-3333-444455556666:3"));
    }

    [Test]
    public void FallsBackToKbKeyForInstalledRow()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [{
          "updateKey": "kb:5034441",
          "kbIds": ["5034441"],
          "title": "Security Update",
          "isInstalled": true,
          "installedAt": "2024-01-15T10:00:00Z"
        }]
        """));

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].UpdateKey, Is.EqualTo("kb:KB5034441"));
        Assert.That(entries[0].IsInstalled, Is.True);
        Assert.That(entries[0].IsPending, Is.False);
        Assert.That(entries[0].InstalledAt, Is.Not.Null);
    }

    [Test]
    public void DropsInvalidEntriesAndCapsList()
    {
        var entries = WindowsUpdateInventoryParser.ParseUpdates(Json("""
        [
          { "title": "sem chave" },
          { "updateKey": "lixo", "title": "chave invalida" },
          { "updateKey": "kb:ABC", "title": "kb invalido" }
        ]
        """));

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public void ParsesScanStatusAndError()
    {
        var scan = WindowsUpdateInventoryParser.ParseScan(Json("""
        { "status": "timeout", "durationMs": 1234, "errorCode": "timeout", "startedAt": "2024-01-15T10:00:00Z" }
        """));

        Assert.That(scan.Status, Is.EqualTo("timeout"));
        Assert.That(scan.DurationMs, Is.EqualTo(1234));
        Assert.That(scan.ErrorCode, Is.EqualTo("timeout"));
        Assert.That(scan.StartedAt, Is.Not.Null);
    }

    [Test]
    public void AcceptsPartialScanStatus()
    {
        var scan = WindowsUpdateInventoryParser.ParseScan(Json("""{ "status": "partial", "errorCode": "qfe_failed" }"""));
        Assert.That(scan.Status, Is.EqualTo("partial"));
    }

    [Test]
    public void UnknownScanStatusFallsBackToOk()
    {
        var scan = WindowsUpdateInventoryParser.ParseScan(Json("""{ "status": "hacked" }"""));
        Assert.That(scan.Status, Is.EqualTo("ok"));
    }

    // ── Bloco opcional "state" (sonda de estado do serviço) ───────────────

    [Test]
    public void MissingStateBlockIsNull()
    {
        Assert.That(WindowsUpdateInventoryParser.ParseState(null), Is.Null);
        Assert.That(WindowsUpdateInventoryParser.ParseState(Json("{}")), Is.Null);
        Assert.That(WindowsUpdateInventoryParser.ParseState(Json("[]")), Is.Null);
    }

    [Test]
    public void ParsesServiceStateBlock()
    {
        var state = WindowsUpdateInventoryParser.ParseState(Json("""
        {
          "serviceState": "Disabled",
          "wuOnline": false,
          "policyManaged": true,
          "lastInstallSuccessAt": "2026-10-16T21:09:57Z",
          "downloadInProgress": true,
          "hash": "abc123",
          "changedAt": "2026-10-18T12:00:00Z"
        }
        """));

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.ServiceState, Is.EqualTo("disabled"));
        Assert.That(state.WuOnline, Is.False);
        Assert.That(state.PolicyManaged, Is.True);
        Assert.That(state.LastInstallSuccessAt, Is.Not.Null);
        Assert.That(state.DownloadInProgress, Is.True);
        Assert.That(state.StateHash, Is.EqualTo("abc123"));
        Assert.That(state.StateChangedAt, Is.Not.Null);
    }

    [Test]
    public void ServiceStateAcceptsAgentAliases()
    {
        var state = WindowsUpdateInventoryParser.ParseState(Json("""
        { "service": "running", "isDownloading": true, "lastSuccessTime": "2026-10-16T21:09:57Z" }
        """));

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.ServiceState, Is.EqualTo("running"));
        Assert.That(state.DownloadInProgress, Is.True);
        Assert.That(state.LastInstallSuccessAt, Is.Not.Null);
    }

    /// <summary>
    /// O envelope REAL do agente usa os nomes tipados do WindowsUpdateState
    /// ("lastInstallSuccess"/"signature"). Sem aceitar esses nomes o campo
    /// aparecia nulo na aba — regressão silenciosa de UI.
    /// </summary>
    [Test]
    public void ParsesStateBlockWithAgentCanonicalFieldNames()
    {
        var state = WindowsUpdateInventoryParser.ParseState(Json("""
        {
          "supported": true,
          "serviceState": "running",
          "wuOnline": true,
          "policyManaged": false,
          "wsusServer": "",
          "rebootPending": true,
          "rebootReasons": ["file_rename"],
          "lastInstallSuccess": "2026-10-16T21:09:57Z",
          "lastInstallTime": "2026-10-16T21:10:02Z",
          "downloadInProgress": true,
          "qfeInstalledCount": 4,
          "recentQfes": ["KB5127690"],
          "signature": "4bea11c0d1",
          "checkedAt": "2026-10-19T22:00:00Z"
        }
        """));

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.ServiceState, Is.EqualTo("running"));
        Assert.That(state.LastInstallSuccessAt, Is.EqualTo(new DateTime(2026, 10, 16, 21, 9, 57, DateTimeKind.Utc)));
        Assert.That(state.StateHash, Is.EqualTo("4bea11c0d1"));
        Assert.That(state.DownloadInProgress, Is.True);
    }

    [Test]
    public void UnknownServiceStateIsDroppedButOtherFieldsKept()
    {
        var state = WindowsUpdateInventoryParser.ParseState(Json("""{ "serviceState": "hacked", "wuOnline": true }"""));
        Assert.That(state, Is.Not.Null);
        Assert.That(state!.ServiceState, Is.Null);
        Assert.That(state.WuOnline, Is.True);
    }

    [Test]
    public void FiltersUnknownRebootReasons()
    {
        var reboot = WindowsUpdateInventoryParser.ParsePendingReboot(Json("""
        { "pending": true, "reasons": ["windows_update", "cbs", "evil"], "checkedAt": "2024-01-15T10:00:00Z" }
        """));

        Assert.That(reboot, Is.Not.Null);
        Assert.That(reboot!.Pending, Is.True);
        Assert.That(reboot.Reasons, Is.EquivalentTo(new[] { "windows_update", "cbs" }));
    }

    // ── Diagnóstico do scan + sonda estendida (M239) ────────────────────────

    [Test]
    public void ParsesScanDiagnostics()
    {
        var meta = WindowsUpdateInventoryParser.ParseScan(Json("""
        {
          "status": "ok",
          "durationMs": 1500,
          "skippedCount": 3,
          "skippedReasons": { "driver": 2, "no_identity": 1, "unreadable": 0, "weird": 9 },
          "trigger": "CHANGE",
          "source": "wua"
        }
        """));

        Assert.That(meta.SkippedCount, Is.EqualTo(3));
        Assert.That(meta.SkippedReasons, Is.Not.Null);
        Assert.That(meta.SkippedReasons!["driver"], Is.EqualTo(2));
        Assert.That(meta.SkippedReasons["no_identity"], Is.EqualTo(1));
        Assert.That(meta.SkippedReasons.ContainsKey("weird"), Is.False, "motivo fora da allowlist é descartado");
        Assert.That(meta.SkippedReasons.ContainsKey("unreadable"), Is.False, "valor 0 não entra no mapa");
        Assert.That(meta.Trigger, Is.EqualTo("change"), "trigger normalizado em minúsculas");
        Assert.That(meta.Source, Is.EqualTo("wua"));
    }

    [Test]
    public void LegacyScanBlockLeavesDiagnosticsNull()
    {
        var meta = WindowsUpdateInventoryParser.ParseScan(Json("""{ "status": "error", "errorCode": "service_disabled" }"""));

        Assert.That(meta.Status, Is.EqualTo("error"));
        Assert.That(meta.ErrorCode, Is.EqualTo("service_disabled"));
        Assert.That(meta.SkippedCount, Is.Null, "agente antigo: null preserva o valor do snapshot");
        Assert.That(meta.SkippedReasons, Is.Null);
        Assert.That(meta.Trigger, Is.Null);
        Assert.That(meta.Source, Is.Null);
    }

    [Test]
    public void RejectsUnknownTriggerSourceAndOutOfRangeSkipCount()
    {
        var meta = WindowsUpdateInventoryParser.ParseScan(Json("""
        { "status": "ok", "skippedCount": 999999, "trigger": "manual", "source": "winget" }
        """));

        Assert.That(meta.SkippedCount, Is.Null);
        Assert.That(meta.Trigger, Is.Null);
        Assert.That(meta.Source, Is.Null);
    }

    [Test]
    public void ParsesExtendedStateProbe()
    {
        var meta = WindowsUpdateInventoryParser.ParseState(Json("""
        {
          "serviceState": "running",
          "wsusServer": "http://wsus.empresa.local:8530",
          "auOptions": 4,
          "noAutoUpdate": false,
          "deferralDays": 7,
          "activeHoursStart": 8,
          "activeHoursEnd": 18,
          "qfeInstalledCount": 42,
          "downloadInProgress": true,
          "downloadPercent": 42,
          "signature": "abc"
        }
        """));

        Assert.That(meta, Is.Not.Null);
        Assert.That(meta!.WsusServer, Is.EqualTo("http://wsus.empresa.local:8530"));
        Assert.That(meta.AuOptions, Is.EqualTo(4));
        Assert.That(meta.NoAutoUpdate, Is.False);
        Assert.That(meta.DeferralDays, Is.EqualTo(7));
        Assert.That(meta.ActiveHoursStart, Is.EqualTo(8));
        Assert.That(meta.ActiveHoursEnd, Is.EqualTo(18));
        Assert.That(meta.QfeInstalledCount, Is.EqualTo(42));
        Assert.That(meta.DownloadPercent, Is.EqualTo(42));
        Assert.That(meta.DownloadInProgress, Is.True);
    }

    [Test]
    public void ExtendedStateIgnoresOutOfRangeValues()
    {
        var meta = WindowsUpdateInventoryParser.ParseState(Json("""
        {
          "wsusServer": "   ",
          "auOptions": 9,
          "deferralDays": 999,
          "activeHoursStart": 24,
          "activeHoursEnd": -1,
          "downloadPercent": 150
        }
        """));

        // Todos os campos inválidos ⇒ nada a persistir (meta null preserva o
        // estado conhecido no snapshot, inclusive o WSUS já configurado).
        Assert.That(meta, Is.Null);
    }

    [Test]
    public void ParsesDownloadPercentZeroAndFull()
    {
        var zero = WindowsUpdateInventoryParser.ParseState(Json("""{ "downloadPercent": 0 }"""));
        var full = WindowsUpdateInventoryParser.ParseState(Json("""{ "downloadPercent": 100 }"""));

        Assert.That(zero!.DownloadPercent, Is.EqualTo(0), "0% é informação válida");
        Assert.That(full!.DownloadPercent, Is.EqualTo(100));
    }
}
