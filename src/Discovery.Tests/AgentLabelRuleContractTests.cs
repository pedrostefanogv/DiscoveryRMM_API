using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Trava o CONTRATO de fio do endpoint de regras.
///
/// A migracao dos controllers para CQRS trocou o DTO de entrada
/// (CreateAgentLabelRuleRequest, com `expression` objeto) pelo command
/// (CreateLabelRuleCommand, com `expressionJson` string) sem atualizar o front.
/// Resultado: criar regra respondia 400 e editar ignorava a expressao em silencio.
/// Estes testes falham se o formato de fio voltar a divergir.
/// </summary>
public class AgentLabelRuleContractTests
{
    /// <summary>Mesmas opcoes do Program.cs (camelCase, case-insensitive, enums por nome).</summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Test]
    public void FrontendPayload_BindsExpressionObjectIntoTypedRequest()
    {
        // Payload exatamente como o editor da UI envia (enums numericos).
        const string payload = """
        {
          "name": "Windows PROD",
          "label": "PROD",
          "description": "estacoes",
          "applyMode": 1,
          "expression": {
            "nodeType": 0,
            "logicalOperator": 0,
            "children": [
              { "nodeType": 1, "field": 3, "operator": 0, "value": "Windows" }
            ]
          }
        }
        """;

        var request = JsonSerializer.Deserialize<CreateAgentLabelRuleRequest>(payload, ApiJson);

        Assert.That(request, Is.Not.Null);
        Assert.That(request!.Expression.NodeType, Is.EqualTo(AgentLabelNodeType.Group));
        Assert.That(request.Expression.Children, Has.Count.EqualTo(1));
        Assert.That(request.Expression.Children[0].Field, Is.EqualTo(AgentLabelField.OperatingSystem));
        Assert.That(request.Expression.Children[0].Value, Is.EqualTo("Windows"));
    }

    [Test]
    public void LabelRuleDto_SerializesExpressionAsObject()
    {
        var dto = new LabelRuleDto(
            Guid.NewGuid(),
            "r",
            "L",
            null,
            true,
            "ApplyOnly",
            "Exact",
            new AgentLabelRuleExpressionNodeDto
            {
                NodeType = AgentLabelNodeType.Group,
                LogicalOperator = AgentLabelLogicalOperator.And,
                Children = [new AgentLabelRuleExpressionNodeDto { NodeType = AgentLabelNodeType.Condition }]
            },
            null,
            DateTime.UtcNow,
            DateTime.UtcNow);

        var json = JsonSerializer.Serialize(dto, ApiJson);

        Assert.That(json, Does.Contain("\"expression\""),
            "O front le 'expression' como objeto; antes a API devolvia 'expressionJson' string.");
        Assert.That(json, Does.Not.Contain("expressionJson"));
    }

    [Test]
    public void ExpressionJson_RoundTrips()
    {
        var original = new AgentLabelRuleExpressionNodeDto
        {
            NodeType = AgentLabelNodeType.DiskGroup,
            LogicalOperator = AgentLabelLogicalOperator.Or,
            Children =
            [
                new AgentLabelRuleExpressionNodeDto
                {
                    NodeType = AgentLabelNodeType.Condition,
                    Field = AgentLabelField.DiskFreeSpacePercent,
                    Operator = AgentLabelComparisonOperator.LessThan,
                    Value = "20"
                }
            ]
        };

        var restored = AgentLabelExpressionJson.DeserializeOrDefault(AgentLabelExpressionJson.Serialize(original));

        Assert.That(restored.NodeType, Is.EqualTo(AgentLabelNodeType.DiskGroup));
        Assert.That(restored.Children, Has.Count.EqualTo(1));
        Assert.That(restored.Children[0].Field, Is.EqualTo(AgentLabelField.DiskFreeSpacePercent));
        Assert.That(restored.Children[0].Value, Is.EqualTo("20"));
    }

    [Test]
    public void ExportDto_AcceptsApplyModeAsNumberOrName()
    {
        // Arquivo de import editado a mao pode usar o valor numerico do enum.
        var byNumber = JsonSerializer.Deserialize<AgentLabelRuleExportDto>("""
        { "name": "r", "label": "L", "applyMode": 0, "expression": { "nodeType": 0, "logicalOperator": 0, "children": [] } }
        """, ApiJson);

        var byName = JsonSerializer.Deserialize<AgentLabelRuleExportDto>("""
        { "name": "r", "label": "L", "applyMode": "ApplyOnly", "expression": { "nodeType": 0, "logicalOperator": 0, "children": [] } }
        """, ApiJson);

        Assert.That(byNumber!.ApplyMode, Is.EqualTo("0"));
        Assert.That(byName!.ApplyMode, Is.EqualTo("ApplyOnly"));
    }

    [Test]
    public void DeserializeOrDefault_WithMalformedJson_ReturnsEmptyNode()
    {
        var restored = AgentLabelExpressionJson.DeserializeOrDefault("{ nao-e-json");

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.Children, Is.Empty);
    }
}
