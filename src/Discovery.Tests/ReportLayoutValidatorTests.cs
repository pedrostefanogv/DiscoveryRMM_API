using Discovery.Core.Helpers;

namespace Discovery.Tests;

public class ReportLayoutValidatorTests
{
    [Test]
    public void ValidateJson_WhenLayoutIsValid_ReturnsNoErrors()
    {
        const string layoutJson = """
        {
          "title": "Software por Agente",
          "orientation": "landscape",
          "groupBy": "agentHostname",
          "hideGroupColumn": true,
          "groupDetails": [
            { "field": "clientName", "header": "Cliente" },
            { "field": "siteName", "header": "Site" }
          ],
          "columns": [
            { "field": "agentHostname", "header": "Agente" },
            { "field": "softwareName", "header": "Software" },
            { "field": "version", "header": "Versao" }
          ],
          "summaries": [
            { "label": "Total", "aggregate": "count" }
          ],
          "groupSummaries": [
            { "label": "Softwares distintos", "field": "softwareName", "aggregate": "countDistinct" }
          ],
          "style": {
            "primaryColor": "#16324F",
            "headerBackgroundColor": "#0B5FFF",
            "alternateRowColor": "#EEF4FF"
          }
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void ValidateJson_WhenColumnsAndSectionsAreMixed_ReturnsNoErrors()
    {
        const string layoutJson = """
        {
          "columns": [ { "field": "agentHostname" } ],
          "sections": [
            {
              "title": "Dados",
              "columns": [ { "field": "softwareName" } ]
            }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void ValidateJson_WhenColumnFormatIsDate_ReturnsNoErrors()
    {
        const string layoutJson = """
        {
          "columns": [
            { "field": "createdAt", "format": "date" }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void ValidateJson_WhenStyleColorIsInvalid_ReturnsError()
    {
        const string layoutJson = """
        {
          "columns": [ { "field": "softwareName" } ],
          "style": {
            "primaryColor": "blue"
          }
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("style.primaryColor"));
    }

    [Test]
    public void ValidateJson_WhenWatermarkDedicatedLogoUrlIsValid_ReturnsNoErrors()
    {
        const string layoutJson = """
        {
          "columns": [ { "field": "softwareName" } ],
          "watermark": {
            "useLogo": true,
            "logoUrl": "https://cdn.exemplo.local/watermark.png",
            "imageFit": "cover",
            "imageOpacity": 0.2
          }
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void ValidateJson_WhenWatermarkImageFitIsInvalid_ReturnsError()
    {
        const string layoutJson = """
        {
          "columns": [ { "field": "softwareName" } ],
          "watermark": {
            "imageFit": "stretch"
          }
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("watermark.imageFit"));
    }

    [Test]
    public void ValidateJson_WhenSummaryRequiresFieldAndDoesNotProvideIt_ReturnsError()
    {
        const string layoutJson = """
        {
          "columns": [ { "field": "softwareName" } ],
          "summaries": [
            { "label": "Soma", "aggregate": "sum" }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Has.Some.Contains("summaries[0].field is required"));
    }

      [Test]
      public void ValidateJson_WhenColumnUsesLabelAndEmptyWidthString_ReturnsNoErrors()
      {
        const string layoutJson = """
        {
          "columns": [
            { "field": "clientId", "label": "Cliente", "width": "" },
            { "field": "siteId", "label": "Site", "width": "40%" }
          ]
        }
        """;

        var errors = ReportLayoutValidator.ValidateJson(layoutJson);

        Assert.That(errors, Is.Empty);
      }

  [Test]
  public void ValidateJson_WhenGroupDetailsExceedLimit_ReturnsError()
  {
    var detailItems = string.Join(",", Enumerable.Range(1, 13).Select(index => $"{{ \"field\": \"field{index}\", \"header\": \"Field {index}\" }}"));
    var layoutJson = $$"""
    {
      "columns": [ { "field": "softwareName" } ],
      "groupDetails": [ {{detailItems}} ]
    }
    """;

    var errors = ReportLayoutValidator.ValidateJson(layoutJson);

    Assert.That(errors, Has.Some.Contains("groupDetails supports at most"));
  }

  [Test]
  public void ValidateJson_WhenDataSourcesAreValid_ReturnsNoErrors()
  {
    const string layoutJson = """
    {
      "dataSources": [
        { "datasetType": "AgentHardware", "alias": "hw" },
        {
          "datasetType": "SoftwareInventory",
          "alias": "sw",
          "join": {
            "joinToAlias": "hw",
            "sourceKey": "agentId",
            "targetKey": "agentId",
            "joinType": "left"
          }
        }
      ],
      "columns": [
        { "field": "hw.agentHostname", "label": "Agente" },
        { "field": "sw.softwareName", "label": "Software" }
      ]
    }
    """;

    var errors = ReportLayoutValidator.ValidateJson(layoutJson);

    Assert.That(errors, Is.Empty);
  }

  [Test]
  public void ValidateJson_WhenDataSourceAliasIsDuplicated_ReturnsError()
  {
    const string layoutJson = """
    {
      "dataSources": [
        { "datasetType": "AgentHardware", "alias": "src" },
        { "datasetType": "SoftwareInventory", "alias": "src", "join": { "sourceKey": "agentId" } }
      ],
      "columns": [ { "field": "src.agentHostname" } ]
    }
    """;

    var errors = ReportLayoutValidator.ValidateJson(layoutJson);

    Assert.That(errors, Has.Some.Contains("is duplicated"));
  }

  // ── Fatia D: fonte principal explicita (base) ─────────────────────────────

  [Test]
  public void ValidateJson_WhenLayoutHasNoBaseMark_StillUsesFirstSourceAsBase()
  {
    // ADITIVO: layout gravado ANTES do campo "base" precisa continuar valido e
    // continuar usando dataSources[0] como principal.
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
  public void ValidateJson_WhenBaseIsMarked_ReturnsNoErrors()
  {
    const string layoutJson = """
    {
      "dataSources": [
        { "datasetType": "AgentHardware", "alias": "hw", "base": true },
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
  public void ValidateJson_WhenBaseIsNotTheFirstSource_ReturnsNoErrors()
  {
    // A base e a SEGUNDA do array: com base:true a ordem deixa de definir quem
    // e a principal. A primeira fonte se liga nela.
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
  public void ValidateJson_WhenTwoSourcesAreMarkedAsBase_ReturnsError()
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
  public void ValidateJson_WhenJoinGraphIsDisconnected_ReturnsError()
  {
    // "audit" se liga a si mesma: nao alcanca a base. Antes o validador so
    // conferia se o alias existia, entao o grafo desconexo passava.
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

  [Test]
  public void ValidateJson_WhenChainedSourcesReachTheBase_ReturnsNoErrors()
  {
    // hw(base) <- sns <- lbl: a cadeia inteira alcanca a base.
    const string layoutJson = """
    {
      "dataSources": [
        { "datasetType": "AgentHardware", "alias": "hw", "base": true },
        {
          "datasetType": "AgentSensors",
          "alias": "sns",
          "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" }
        },
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
}