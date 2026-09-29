using System.Text.Json;
using Discovery.Core.DTOs;

namespace Discovery.Core.Helpers;

/// <summary>
/// Serializacao/deserializacao tolerante da expressao de uma regra de label.
///
/// Centraliza o formato para que request (objeto), persistencia (jsonb) e response
/// (objeto) nao divirjam. Foi exatamente essa divergencia que quebrou o contrato na
/// migracao dos controllers para CQRS: o front continuou enviando `expression`
/// (objeto) enquanto o command passou a esperar `expressionJson` (string).
/// </summary>
public static class AgentLabelExpressionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(AgentLabelRuleExpressionNodeDto? expression)
        => JsonSerializer.Serialize(expression ?? new AgentLabelRuleExpressionNodeDto(), Options);

    /// <summary>
    /// Nunca lanca: expressao ausente/corrompida vira um no vazio. Espelha o
    /// comportamento tolerante que o controller antigo tinha em MapRule.
    /// </summary>
    public static AgentLabelRuleExpressionNodeDto DeserializeOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new AgentLabelRuleExpressionNodeDto();

        try
        {
            return JsonSerializer.Deserialize<AgentLabelRuleExpressionNodeDto>(json, Options)
                ?? new AgentLabelRuleExpressionNodeDto();
        }
        catch (JsonException)
        {
            return new AgentLabelRuleExpressionNodeDto();
        }
    }
}
