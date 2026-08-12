using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class ReportDatasetCatalogProviderTests
{
    private readonly IReportDatasetCatalogProvider _provider = new ReportDatasetCatalogProvider();

    [Test]
    public void GetAll_ReturnsAllDatasets()
    {
        var catalog = _provider.GetAll();

        Assert.That(catalog, Is.Not.Null);
        Assert.That(catalog.Count, Is.EqualTo(23));
    }

    [Test]
    public void GetAll_EveryDatasetHasKeyTypeAndFields()
    {
        var catalog = _provider.GetAll();

        foreach (var item in catalog)
        {
            Assert.That(item.Key, Is.Not.Empty, $"{item.Name}: key vazia");
            Assert.That(item.Type, Is.Not.Empty, $"{item.Name}: type vazia");
            Assert.That(item.Name, Is.Not.Empty, $"{item.Key}: nome vazio");
            Assert.That(item.Fields, Is.Not.Empty, $"{item.Key}: sem campos");
            Assert.That(item.SupportedFormats, Is.Not.Empty, $"{item.Key}: sem formatos");
        }
    }

    [Test]
    public void GetAll_DefaultAliasesAreUnique()
    {
        var catalog = _provider.GetAll();

        var aliases = catalog
            .SelectMany(i => i.FieldMetadata)
            .Select(m => m.DefaultAlias)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        var duplicates = aliases
            .GroupBy(a => a)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.That(duplicates, Is.Empty, $"Aliases duplicados: {string.Join(", ", duplicates)}");
    }

    [Test]
    public void GetAll_JoinCapabilitiesReferToRealFields()
    {
        var catalog = _provider.GetAll();

        foreach (var item in catalog)
        {
            var fieldSet = item.Fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in item.JoinCapabilities)
            {
                Assert.That(fieldSet, Does.Contain(cap.SourceKey),
                    $"{item.Key}: join key '{cap.SourceKey}' não existe nos campos");
                Assert.That(fieldSet, Does.Contain(cap.TargetKey),
                    $"{item.Key}: join target '{cap.TargetKey}' não existe nos campos");
            }
        }
    }

    [Test]
    public void GetAll_AgentScopedDatasetsExposeAgentIdField()
    {
        var catalog = _provider.GetAll();

        var agentJoinedKeys = new[]
        {
            "softwareInventory", "logs", "tickets", "agentHardware",
            "agentInventoryComposite", "agentLabels", "automationExecutions",
            "agentMonitoringEvents", "p2pTelemetry", "agentDisks",
            "networkAdapters", "listeningPorts", "printers",
        };

        foreach (var key in agentJoinedKeys)
        {
            var item = catalog.FirstOrDefault(i => i.Key == key);
            Assert.That(item, Is.Not.Null, $"Dataset '{key}' não encontrado");

            var exposesAgentId = item!.Fields.Contains("agentId", StringComparer.OrdinalIgnoreCase)
                || item.Fields.Contains("agentHostname", StringComparer.OrdinalIgnoreCase);
            Assert.That(exposesAgentId, Is.True, $"{key}: deveria expor agentId/agentHostname para join");
        }
    }
}