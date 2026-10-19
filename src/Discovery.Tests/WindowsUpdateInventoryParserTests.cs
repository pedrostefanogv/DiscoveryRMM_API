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
}
