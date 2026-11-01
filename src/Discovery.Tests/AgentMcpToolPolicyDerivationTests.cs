using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// A allowlist de tools MCP enviada ao agent (payload mcpToolPolicy) é derivada
/// das políticas efetivas de governança (mcp_tool_policies) — a tela
/// /settings/mcp-tools é a única fonte de verdade. O campo legado
/// mcp_tool_policy_json das configurações não é mais consumido.
/// </summary>
public class AgentMcpToolPolicyDerivationTests
{
    private static McpToolPolicy Policy(string name, bool enabled) => new()
    {
        ToolName = name,
        IsEnabled = enabled,
    };

    [Test]
    public void Sem_politicas_retorna_null_mode_all_compat()
    {
        Assert.That(GetAgentConfigurationHandler.DeriveMcpToolPolicy([]), Is.Null);
    }

    [Test]
    public void Todas_habilitadas_retorna_null_zero_mudanca_de_comportamento()
    {
        var policies = new[] { Policy("ping", true), Policy("get_inventory", true) };
        Assert.That(GetAgentConfigurationHandler.DeriveMcpToolPolicy(policies), Is.Null);
    }

    [Test]
    public void Com_tool_desabilitada_envia_allowlist_das_habilitadas()
    {
        var policies = new[]
        {
            Policy("ping", true),
            Policy("get_inventory", true),
            Policy("install_update_all", false),
        };

        var derived = GetAgentConfigurationHandler.DeriveMcpToolPolicy(policies);

        Assert.That(derived, Is.Not.Null);
        var payload = ToJsonObject(derived!);
        Assert.That(payload["mode"]!.ToString(), Is.EqualTo("allowlist"));
        var allow = (Newtonsoft.Json.Linq.JArray)payload["allow"]!;
        Assert.That(
            allow.Select(a => a.ToString()),
            Is.EquivalentTo(new[] { "ping", "get_inventory" }));
    }

    [Test]
    public void Todas_desabilitadas_gera_allowlist_vazia_bloqueia_tudo()
    {
        // Comportamento coerente com a governança: com TODAS as tools
        // desabilitadas o servidor também não oferece nenhuma ao LLM, e o
        // agent recusa qualquer execução local (tool_not_allowed).
        var policies = new[] { Policy("ping", false), Policy("get_inventory", false) };

        var payload = ToJsonObject(GetAgentConfigurationHandler.DeriveMcpToolPolicy(policies)!);

        Assert.That(payload["mode"]!.ToString(), Is.EqualTo("allowlist"));
        Assert.That((Newtonsoft.Json.Linq.JArray)payload["allow"]!, Is.Empty);
    }

    /// <summary>Converte o payload anônimo em JObject para inspeção dos campos.</summary>
    private static Newtonsoft.Json.Linq.JObject ToJsonObject(object value) =>
        Newtonsoft.Json.Linq.JObject.FromObject(value);
}
