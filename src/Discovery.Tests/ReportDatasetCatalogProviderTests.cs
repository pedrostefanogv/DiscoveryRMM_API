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
        // Um item de catálogo por valor de ReportDatasetType. A asserção pelo
        // enum (e não por número mágico) evita este teste quebrar a cada
        // dataset novo e continua pegando dataset esquecido no catálogo.
        var expected = Enum.GetValues<Discovery.Core.Enums.ReportDatasetType>().Length;
        Assert.That(catalog.Select(i => i.Type).Distinct().Count(), Is.EqualTo(expected),
            "todo ReportDatasetType precisa ter entrada no catálogo");
        Assert.That(catalog.Count, Is.EqualTo(expected));
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
    public void Catalog_JoinCapabilitiesAreCrossDataset()
    {
        var catalog = _provider.GetAll();

        var hardware = catalog.Single(i => i.Key == "agentHardware");
        var sensors = catalog.Single(i => i.Key == "agentSensors");

        // A regressao original: SourceKey == TargetKey == k (sempre k->k), sem
        // par origem->destino, deixava o wizard sem NENHUMA sugestao de join
        // entre hw e sns mesmo os dois compartilhando agentId.
        Assert.That(hardware.JoinCapabilities, Is.Not.Empty,
            "agentHardware precisa de capacidades de join cruzadas");
        Assert.That(sensors.JoinCapabilities, Is.Not.Empty,
            "agentSensors precisa de capacidades de join cruzadas");

        var agentId = hardware.JoinCapabilities
            .FirstOrDefault(cap => string.Equals(cap.SourceKey, "agentId", StringComparison.OrdinalIgnoreCase));

        Assert.That(agentId, Is.Not.Null, "hw x sns precisa expor a chave agentId");
        Assert.That(agentId!.TargetKey, Is.EqualTo("agentId"));
        Assert.That(agentId.JoinTypes, Does.Contain("left"));
        Assert.That(agentId.JoinTypes, Does.Contain("inner"));
        Assert.That(agentId.Description, Is.Not.Null.And.Not.Empty,
            "a capacidade precisa dizer com qual dataset a chave casa");

        // Toda capacidade continua referenciando campos reais dos dois lados.
        foreach (var item in catalog)
        {
            var fields = item.Fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cap in item.JoinCapabilities)
            {
                Assert.That(fields, Does.Contain(cap.SourceKey), $"{item.Key}: source key inexistente");
                Assert.That(fields, Does.Contain(cap.TargetKey), $"{item.Key}: target key inexistente");
            }
        }
    }

    [Test]
    public void Catalog_JoinCapabilitiesAreBoundedAndTargetOtherDatasets()
    {
        var catalog = _provider.GetAll();

        var total = catalog.Sum(i => i.JoinCapabilities.Count);
        Assert.That(total, Is.LessThanOrEqualTo(200), "teto de 200 sugestoes (padrao do builder)");

        // O teto e GLOBAL: a distribuicao precisa ser justa, senao os primeiros
        // datasets do catalogo consomem o orcamento e os ultimos ficam sem
        // capacidade alguma.
        // "Precisa ter capacidade" = expoe alguma chave marcada como join key
        // que outro dataset tambem exponha. Datasets sem chave de join real
        // (ex: configurationAudit declara clientId/siteId mas nao os expoe em
        // Fields) legitimamente ficam sem capacidade.
        var semCapacidade = catalog
            .Where(i => i.JoinCapabilities.Count == 0)
            .Where(i =>
            {
                var minhasChaves = i.FieldMetadata
                    .Where(m => m.IsJoinKey && !string.IsNullOrWhiteSpace(m.Field))
                    .Select(m => m.Field)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (minhasChaves.Count == 0) return false;

                return catalog.Any(outro =>
                    !string.Equals(outro.Key, i.Key, StringComparison.OrdinalIgnoreCase)
                    && outro.Fields.Any(minhasChaves.Contains));
            })
            .Select(i => i.Key)
            .ToList();
        Assert.That(semCapacidade, Is.Empty,
            "datasets com campo compartilhado ficaram sem capacidade: " + string.Join(", ", semCapacidade));

        var orphanKeys = catalog
            .SelectMany(i => i.JoinCapabilities)
            .Where(cap => string.Equals(cap.SourceKey, cap.TargetKey, StringComparison.OrdinalIgnoreCase) == false)
            .ToList();
        Assert.That(orphanKeys, Is.Empty, "capacidade precisa preservar o par k->k do contrato");

        // Um dataset que compartilha chave com outro precisa enxergar um par
        // cujo TargetKey existe nos campos do PARCEIRO, nao so nos proprios.
        var hardware = catalog.Single(i => i.Key == "agentHardware");
        var sensors = catalog.Single(i => i.Key == "agentSensors");
        var sensorFields = sensors.Fields.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.That(
            hardware.JoinCapabilities.Any(cap => sensorFields.Contains(cap.TargetKey)),
            Is.True,
            "as capacidades de hw precisam apontar para chaves que existem no sns");
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
            // Patches/agentes: fontes novas precisam expor agentId/hostname.
            "windowsUpdates", "patchCompliance", "agents",
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