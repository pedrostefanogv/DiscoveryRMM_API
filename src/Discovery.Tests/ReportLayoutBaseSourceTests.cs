using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Fatia D: a fonte principal passa a poder ser MARCADA (base=true) em vez de
/// ser sempre dataSources[0]. Sem a marca, nada muda — e esse o ponto critico
/// de compatibilidade com o que ja esta gravado.
/// </summary>
public class ReportLayoutBaseSourceTests
{
    [Test]
    public void ValidateJson_WithoutBaseMark_UsesFirstSourceAsBase()
    {
        // Layout ANTIGO, sem "base": precisa continuar valido.
        const string layoutJson = """
        {
          "dataSources": [
            { "datasetType": "AgentHardware", "alias": "hw" },
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void ValidateJson_WithSingleBaseMark_ReturnsNoErrors()
    {
        // A base e a SEGUNDA do array; a primeira liga nela.
        const string layoutJson = """
        {
          "dataSources": [
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            },
            { "datasetType": "AgentHardware", "alias": "hw", "base": true }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void ValidateJson_WithTwoBaseMarks_ReturnsError()
    {
        const string layoutJson = """
        {
          "dataSources": [
            { "datasetType": "AgentHardware", "alias": "hw", "base": true },
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "base": true,
              "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("apenas UMA fonte pode ser marcada como base"));
    }

    [Test]
    public void ValidateJson_WhenGraphIsDisconnected_ReturnsError()
    {
        // hw(0) - sns(1) formam um grupo; audit(2) se liga a um no que nao
        // alcanca a base -> grafo desconexo. Antes isso passava porque o
        // validador so conferia se o alias existia.
        const string layoutJson = """
        {
          "dataSources": [
            { "datasetType": "AgentHardware", "alias": "hw" },
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            },
            {
              "datasetType": "ConfigurationAudit",
              "alias": "audit",
              "join": { "joinToAlias": "audit", "sourceKey": "entityId", "targetKey": "entityId", "joinType": "left" }
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("nao alcanca a base"),
            "grafo desconexo precisa ser reprovado: " + string.Join(" | ", errors));
    }

    [Test]
    public void ValidateJson_WhenBaseIsMarkedInTheMiddle_AllOtherSourcesStillReachIt()
    {
        // Base marcada no meio; as outras duas apontam (direta ou indiretamente)
        // para ela.
        const string layoutJson = """
        {
          "dataSources": [
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            },
            { "datasetType": "AgentHardware", "alias": "hw", "base": true },
            {
              "datasetType": "AgentLabels",
              "alias": "lbl",
              "join": { "joinToAlias": "sns", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void ValidateJson_WhenSourceJoinsItself_ReturnsError()
    {
        const string layoutJson = """
        {
          "dataSources": [
            { "datasetType": "AgentHardware", "alias": "hw" },
            {
              "datasetType": "AgentSensors",
              "alias": "sns",
              "join": { "joinToAlias": "sns", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("nao pode se ligar a si mesma"));
    }
}
