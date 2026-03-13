using Meduza.Core.Helpers;

namespace Meduza.Tests;

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
    public void ValidateJson_WhenColumnsAndSectionsAreMixed_ReturnsError()
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

        Assert.That(errors, Has.Some.Contains("either 'columns' or 'sections'"));
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
}