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

    [Test]
    public void Heartbeat_WithLoggedUser_Parses()
    {
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","loggedUser":"CORP\\pedro"}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.LoggedUser, Is.EqualTo(@"CORP\pedro"));
    }

    [Test]
    public void Heartbeat_WithoutLoggedUser_IsNull_BackwardCompatible()
    {
        // Agentes antigos (sem o campo) devem desserializar sem erro.
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","cpuPercent":10}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.LoggedUser, Is.Null);
    }

    [Test]
    public void Heartbeat_WithEmptyLoggedUser_MeansSupportedButNoSession()
    {
        // Agent novo sem sessão interativa envia o campo vazio (sem omitempty).
        // Precisa ser distinguível do agente antigo (null) para a UI não cair
        // indevidamente no último usuário conhecido.
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","loggedUser":""}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.LoggedUser, Is.Empty);
    }

    [Test]
    public void Heartbeat_WithLoggedUserSince_Parses()
    {
        var json = """{"agentId":"00000000-0000-0000-0000-000000000001","loggedUser":"CORP\\pedro","loggedUserSince":"2026-01-02T03:04:05Z"}""";
        var hb = JsonSerializer.Deserialize<AgentHeartbeat>(json, Options);
        Assert.That(hb, Is.Not.Null);
        Assert.That(hb!.LoggedUserSince, Is.EqualTo(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
    }
}
