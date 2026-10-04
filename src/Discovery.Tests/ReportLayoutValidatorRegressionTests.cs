using Discovery.Core.Helpers;

namespace Discovery.Tests;

public class ReportLayoutValidatorRegressionTests
{
    private const string RealWorldLayout = """
{"title":"Novo Relatório","orientation":"landscape","groupBy":"hw.agentId","hideGroupColumn":true,"columns":[{"field":"hw.agentHostname","header":"Hostname","format":"text","align":"left"},{"field":"hw.totalMemoryGB","header":"RAM (GB)","format":"number","align":"right"},{"field":"hw.osName","header":"SO","format":"text","align":"left"}],"style":{"primaryColor":"#16324F","headerBackgroundColor":"#16324F","headerTextColor":"#FFFFFF","alternateRowColor":"#EEF4F7","fontFamily":"Segoe UI, sans-serif","showRowStripes":true},"dataSources":[{"datasetType":4,"alias":"hw"},{"datasetType":3,"alias":"tk","join":{"joinToAlias":"hw","sourceKey":"agentId","targetKey":"agentId","joinType":"left"}}],"sections":[{"title":"Chamados (Tickets)","source":"tk","columns":[{"field":"tk.title","header":"Título","format":"text","align":"left"},{"field":"tk.priority","header":"Prioridade","format":"text","align":"center"},{"field":"tk.createdAt","header":"Aberto em","format":"datetime","align":"left"}]}]}
""";

    [Test]
    public void ValidateJson_RealWorldMultiSourceLayout_IsValid()
    {
        var errors = ReportLayoutValidator.ValidateJson(RealWorldLayout);
        Assert.That(errors, Is.Empty, "erros: " + string.Join(" | ", errors));
    }
}
