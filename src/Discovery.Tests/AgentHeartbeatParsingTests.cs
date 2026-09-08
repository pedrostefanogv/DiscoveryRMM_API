using System.Text.Json;
using Discovery.Core.DTOs;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes de parsing do heartbeat do agent (revisão 3 — campo uiOnline da
/// separação serviço × UI, PLANO_SEPARACAO_SERVICO_UI.md).
/// </summary>
public class AgentHeartbeatParsingTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Test]
    public void Heartbeat_WithUiOnline_True_Parses()
    {
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","cpuPercent":12.5,"uiOnline":true}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.UiOnline, Is.True);
    }

    [Test]
    public void Heartbeat_WithUiOnline_False_Parses()
    {
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","uiOnline":false}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.UiOnline, Is.False);
    }

    [Test]
    public void Heartbeat_WithoutUiOnline_IsNull_BackwardCompatible()
    {
        // Agentes antigos/standalone não enviam uiOnline (omitempty) — deve
        // desserializar como null sem erro (retrocompatibilidade).
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","cpuPercent":10}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.UiOnline, Is.Null);
    }
}
