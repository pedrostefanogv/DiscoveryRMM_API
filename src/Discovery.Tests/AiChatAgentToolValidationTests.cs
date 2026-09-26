using System.Text.Json;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão B17: a validação de argumentos de tools do agent rejeitava "{}"
/// para TODA tool (exceto list_ticket_templates), inclusive tools sem
/// parâmetros (get_pending_updates, get_package_actions, ...). O modelo repetia
/// a chamada e o orquestrador caía no fallback seco, produzindo um turno 200
/// sem nenhum token. A validação agora é dirigida por schema (required).
/// </summary>
public class AiChatAgentToolValidationTests
{
    private static JsonElement Schema(params string[] required)
    {
        var reqJson = required.Length == 0
            ? string.Empty
            : ",\"required\":[" + string.Join(",", System.Array.ConvertAll(required, r => "\"" + r + "\"")) + "]";
        var json = "{\"type\":\"object\",\"properties\":{}" + reqJson + "}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Test]
    public void NoArgTool_WithEmptyObject_IsValid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("get_pending_updates", "{}", Schema());
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void NoArgTool_WithMissingSchema_IsValid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("get_package_actions", "", null);
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void EmptyKeyWrapper_IsNormalized()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "search_packages", "{\"\":{\"query\":\"7-zip\"}}", Schema("query"));
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void JsonEncodedString_IsNormalized()
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize("{\"query\":\"chrome\"}");
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "search_packages", encoded, Schema("query"));
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void MissingRequired_IsInvalid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{}", Schema("query"));
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void HardcodedFallback_WhenSchemaUnknown_RejectsMissingQuery()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{}", null);
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void MalformedJson_IsInvalid()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments("search_packages", "{not json", Schema("query"));
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void TrailingComma_IsRepaired()
    {
        var (isValid, _) = AiChatToolOrchestrator.ValidateAgentToolArguments(
            "create_ticket", "{\"title\":\"x\",\"description\":\"y\",}", Schema("title", "description"));
        Assert.That(isValid, Is.True);
    }
}
