using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260528_121)]
public class M121_SeedBuiltInReportTemplates : Migration
{
    private const int SoftwareInventoryVal = 0;
    private const int AgentHardwareVal = 4;
    private const int AgentLabelsVal = 6;
    private const int AutomaticLabelRulesVal = 7;
    private const int TicketsVal = 3;
    private const int AutomationExecutionsVal = 8;

    private const string DefaultStyle = @",""style"":{""primaryColor"":""#16324F"",""headerBackgroundColor"":""#16324F"",""headerTextColor"":""#FFFFFF"",""alternateRowColor"":""#EEF4F7"",""fontFamily"":""Segoe UI, sans-serif""}";

    public override void Up()
    {
        var now = DateTime.UtcNow.ToString("O");

        Seed("a0000001-0000-0000-0000-000000000001",
            "Dispositivos + Software + Labels",
            "Lista dispositivos com hardware, softwares instalados e labels aplicadas.",
            AgentHardwareVal,
            @"{""title"":""Inventario de Dispositivos"",""subtitle"":""Hardware, Software e Labels"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""Dispositivo: {{agentHostname}}"",""hideGroupColumn"":true,""dataSources"":[{""datasetType"":""AgentHardware"",""alias"":""hw""},{""datasetType"":""SoftwareInventory"",""alias"":""sw"",""join"":{""joinToAlias"":""hw"",""sourceKey"":""agentId"",""targetKey"":""agentId"",""joinType"":""left""}},{""datasetType"":""AgentLabels"",""alias"":""lbl"",""join"":{""joinToAlias"":""hw"",""sourceKey"":""agentId"",""targetKey"":""agentId"",""joinType"":""left""}}],""columns"":[{""field"":""hw.agentHostname"",""header"":""Hostname""},{""field"":""hw.totalMemoryGB"",""header"":""RAM (GB)"",""format"":""number""},{""field"":""hw.osName"",""header"":""Sistema Operacional""},{""field"":""hw.processor"",""header"":""Processador""}],""sections"":[{""title"":""Softwares Instalados"",""columns"":[{""field"":""sw.softwareName"",""header"":""Software""},{""field"":""sw.publisher"",""header"":""Fabricante""},{""field"":""sw.version"",""header"":""Versao""}]},{""title"":""Labels"",""columns"":[{""field"":""lbl.labelName"",""header"":""Label""},{""field"":""lbl.labelSource"",""header"":""Origem""}]}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000002",
            "Labels -> Agents",
            "Lista regras de labels automaticas e quais agents cada uma afeta.",
            AutomaticLabelRulesVal,
            @"{""title"":""Labels Automaticas -> Agents"",""subtitle"":""Regras e dispositivos afetados"",""orientation"":""landscape"",""groupBy"":""labelName"",""groupTitleTemplate"":""Label: {{labelName}} ({{matchCount}} agents)"",""hideGroupColumn"":true,""groupDetails"":[{""field"":""ruleName"",""header"":""Regra""},{""field"":""ruleDescription"",""header"":""Descricao""},{""field"":""isActive"",""header"":""Ativa""}],""columns"":[{""field"":""labelName"",""header"":""Label""},{""field"":""ruleName"",""header"":""Regra""},{""field"":""matchCount"",""header"":""Agents Afetados"",""format"":""number""},{""field"":""affectedAgentHostnames"",""header"":""Hostnames""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000003",
            "Software por Maquina",
            "Softwares instalados agrupados por dispositivo.",
            SoftwareInventoryVal,
            @"{""title"":""Software por Maquina"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{agentHostname}} ({{count}} softwares)"",""hideGroupColumn"":true,""columns"":[{""field"":""agentHostname"",""header"":""Hostname""},{""field"":""softwareName"",""header"":""Software""},{""field"":""publisher"",""header"":""Fabricante""},{""field"":""version"",""header"":""Versao""},{""field"":""lastSeenAt"",""header"":""Ultima Visualizacao"",""format"":""datetime""}],""groupDetails"":[{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""}],""groupSummaries"":[{""label"":""Softwares distintos"",""field"":""softwareName"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000004",
            "Inventario de Hardware",
            "Inventario completo de hardware de todos os agentes.",
            AgentHardwareVal,
            @"{""title"":""Inventario de Hardware"",""subtitle"":""Especificacoes tecnicas dos dispositivos"",""orientation"":""landscape"",""columns"":[{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""},{""field"":""agentHostname"",""header"":""Hostname""},{""field"":""osName"",""header"":""SO""},{""field"":""osVersion"",""header"":""Versao SO""},{""field"":""processor"",""header"":""Processador""},{""field"":""processorCores"",""header"":""Cores"",""format"":""number""},{""field"":""totalMemoryGB"",""header"":""RAM (GB)"",""format"":""number""},{""field"":""totalDisksCount"",""header"":""Discos"",""format"":""number""},{""field"":""collectedAt"",""header"":""Coleta"",""format"":""datetime""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000005",
            "Distribuicao de SO",
            "Distribuicao de sistemas operacionais com contagem de dispositivos.",
            AgentHardwareVal,
            @"{""title"":""Distribuicao de Sistemas Operacionais"",""orientation"":""portrait"",""groupBy"":""osName"",""groupTitleTemplate"":""{{osName}} ({{count}} dispositivos)"",""hideGroupColumn"":true,""columns"":[{""field"":""osName"",""header"":""Sistema Operacional""},{""field"":""osVersion"",""header"":""Versao""},{""field"":""agentHostname"",""header"":""Hostname""},{""field"":""collectedAt"",""header"":""Ultima Coleta"",""format"":""datetime""}],""summaries"":[{""label"":""Total de dispositivos"",""field"":""agentHostname"",""aggregate"":""countDistinct""},{""label"":""SOs distintos"",""field"":""osName"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000006",
            "Visao Geral do Site",
            "Resumo de hardware e software por site.",
            AgentHardwareVal,
            @"{""title"":""Visao Geral por Site"",""orientation"":""landscape"",""groupBy"":""siteName"",""groupTitleTemplate"":""Site: {{siteName}} ({{count}} dispositivos)"",""hideGroupColumn"":true,""dataSources"":[{""datasetType"":""AgentHardware"",""alias"":""hw""},{""datasetType"":""SoftwareInventory"",""alias"":""sw"",""join"":{""joinToAlias"":""hw"",""sourceKey"":""agentId"",""targetKey"":""agentId"",""joinType"":""left""}}],""columns"":[{""field"":""hw.agentHostname"",""header"":""Hostname""},{""field"":""hw.osName"",""header"":""SO""},{""field"":""hw.totalMemoryGB"",""header"":""RAM (GB)"",""format"":""number""},{""field"":""hw.processor"",""header"":""Processador""}],""sections"":[{""title"":""Softwares do Site"",""columns"":[{""field"":""sw.softwareName"",""header"":""Software""},{""field"":""sw.publisher"",""header"":""Fabricante""}]}],""groupDetails"":[{""field"":""clientName"",""header"":""Cliente""}],""groupSummaries"":[{""label"":""Dispositivos"",""field"":""hw.agentHostname"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000007",
            "Automacao por Dispositivo",
            "Execucoes de scripts de automacao agrupadas por dispositivo.",
            AutomationExecutionsVal,
            @"{""title"":""Execucoes de Automacao"",""subtitle"":""Scripts executados por dispositivo"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{agentHostname}} ({{count}} execucoes)"",""hideGroupColumn"":true,""columns"":[{""field"":""agentHostname"",""header"":""Hostname""},{""field"":""status"",""header"":""Status""},{""field"":""exitCode"",""header"":""Exit Code"",""format"":""number""},{""field"":""sourceType"",""header"":""Tipo""},{""field"":""createdAt"",""header"":""Inicio"",""format"":""datetime""},{""field"":""completedAt"",""header"":""Conclusao"",""format"":""datetime""}],""groupDetails"":[{""field"":""siteName"",""header"":""Site""}]" + DefaultStyle + "}",
            now);

        Seed("a0000001-0000-0000-0000-000000000008",
            "Chamados por Dispositivo",
            "Chamados abertos agrupados por dispositivo com dados de hardware.",
            TicketsVal,
            @"{""title"":""Chamados por Dispositivo"",""orientation"":""landscape"",""groupBy"":""agentId"",""groupTitleTemplate"":""Dispositivo ({{count}} chamados)"",""hideGroupColumn"":true,""dataSources"":[{""datasetType"":""Tickets"",""alias"":""tk""},{""datasetType"":""AgentHardware"",""alias"":""hw"",""join"":{""joinToAlias"":""tk"",""sourceKey"":""agentId"",""targetKey"":""agentId"",""joinType"":""left""}}],""columns"":[{""field"":""tk.title"",""header"":""Titulo""},{""field"":""tk.priority"",""header"":""Prioridade""},{""field"":""tk.slaBreached"",""header"":""SLA Violado""},{""field"":""tk.createdAt"",""header"":""Aberto em"",""format"":""datetime""},{""field"":""tk.closedAt"",""header"":""Fechado em"",""format"":""datetime""}],""groupDetails"":[{""field"":""hw.osName"",""header"":""SO""},{""field"":""hw.totalMemoryGB"",""header"":""RAM (GB)""}]" + DefaultStyle + "}",
            now);
    }

    public override void Down()
    {
        for (var i = 1; i <= 8; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000001-0000-0000-0000-00000000000{i}") });
        }
    }

    private void Seed(string id, string name, string description, int datasetType, string layoutJson, string now)
    {
        Insert.IntoTable("report_templates").Row(new
        {
            id = Guid.Parse(id),
            name,
            description,
            dataset_type = datasetType,
            default_format = 0,
            layout_json = layoutJson,
            is_active = true,
            is_built_in = true,
            version = 1,
            created_at = now,
            updated_at = now
        });
    }
}